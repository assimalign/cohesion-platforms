using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Compiles platform-neutral resource plans into Docker Engine objects and supervises their
/// observed lifecycle.
/// </summary>
/// <remarks>
/// Normal stop preserves containers, the application network, and named volumes. Teardown removes
/// owned containers and claim volumes in reverse order, then removes an unused owned network.
/// </remarks>
public sealed class DockerGateway :
    ApplicationGateway,
    IDockerComposeRenderer,
    IApplicationGatewayRenderer
{
    private const string ExitCodeContract = "cohesion/sysexits/v1";

    private readonly object _engineGate = new();
    private readonly DockerGatewayOptions _options;
    private readonly DockerPlanCompiler _compiler = new();
    private readonly Func<IDockerEngineClient> _engineFactory;
    private readonly DockerContainerObserver _observer;
    private readonly DockerPlanController _planController;
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;

    private readonly Dictionary<IApplicationResource, IApplicationModel> _imageModels = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IApplicationModel, string?> _publishedIndexes = new(ReferenceEqualityComparer.Instance);
    private IDockerEngineClient? _engine;

    /// <summary>Initializes a Docker gateway with default options.</summary>
    public DockerGateway()
        : this(new DockerGatewayOptions())
    {
    }

    /// <summary>Initializes a Docker gateway with the given options.</summary>
    /// <param name="options">The Docker connection, image, observation, and restart options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A Docker option is invalid.</exception>
    /// <exception cref="NotSupportedException">The Docker Engine endpoint scheme is unsupported.</exception>
    public DockerGateway(DockerGatewayOptions options)
        : this(options, () => new DockerEngineClient(ResolveEngineEndpoint(options)))
    {
    }

    internal DockerGateway(
        DockerGatewayOptions options,
        Func<IDockerEngineClient> engineFactory)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engineFactory);
        options.ValidateDocker();
        _options = options;
        _engineFactory = engineFactory;
        DockerPlanController? controller = null;
        _observer = new DockerContainerObserver(
            options,
            GetEngine,
            (context, compilation, cancellationToken) =>
                (controller ?? throw new InvalidOperationException(
                    "The Docker plan controller has not been initialized."))
                .RestartAsync(context, compilation, cancellationToken));
        controller = new DockerPlanController(options, _compiler, GetEngine, _observer);
        _planController = controller;
        _controllers = [_planController];
    }

    /// <inheritdoc/>
    public override ResourceName Name => "docker";

    /// <inheritdoc/>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State { get; } =
        new InMemoryResourceStateManager();

    /// <inheritdoc/>
    protected override TimeSpan ReadinessBudget => _options.ReadinessBudget;

    /// <inheritdoc/>
    protected override ValueTask<Uri> ResolveControlPlaneAddressAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        return _options.ControlPlaneAddress is Uri address
            ? ValueTask.FromResult(address)
            : base.ResolveControlPlaneAddressAsync(model, cancellationToken);
    }

    /// <inheritdoc/>
    protected override void ValidateResource(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlan plan)
    {
        base.ValidateResource(model, descriptor, plan);
        if (descriptor.Resource is IExternalResource)
        {
            return;
        }

        _compiler.Validate(plan);
        if (descriptor.Resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be realized by Docker because it does not expose an {nameof(IManifestResource)} manifest.");
        }

        string? image = manifestResource.Manifest.Artifact.Image;
        if (image is not null)
        {
            _ = ContainerImageArtifacts.Create(descriptor.Resource.Id, image);
        }

        _imageModels[descriptor.Resource] = model;

        _ = DockerPlanController.ReadRestartPolicy(plan, descriptor.Resource);
        string exitCodes = manifestResource.Manifest.Lifecycle.ExitCodes;
        if (!string.Equals(exitCodes, ExitCodeContract, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Docker resource '{descriptor.Resource.Name}' declares unsupported exit-code contract '{exitCodes}'. Expected '{ExitCodeContract}'.");
        }
    }

    /// <inheritdoc/>
    protected override async Task<IResourceArtifact> GatherAsync(
        IApplicationResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GatherImageAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _publishedIndexes.Clear();
            throw;
        }
    }

    private async Task<IResourceArtifact> GatherImageAsync(
        IApplicationResource resource,
        CancellationToken cancellationToken)
    {
        if (resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot be realized by Docker because it does not expose an {nameof(IManifestResource)} manifest.");
        }

        string? imageReference = manifestResource.Manifest.Artifact.Image;
        IApplicationModel model = _imageModels[resource];
        if (!_publishedIndexes.TryGetValue(model, out string? preparedIndex))
        {
            preparedIndex = await ContainerImagePublishing.PrepareAsync(model, _options.ImageIndexPath,
                async token => (await GetEngine().GetVersionAsync(token).ConfigureAwait(false)).Arch
                    ?? throw new InvalidOperationException("The Docker engine returned no architecture."),
                ContainerImagePublishing.CreatePublisher(), cancellationToken).ConfigureAwait(false);
            _publishedIndexes[model] = preparedIndex;
        }
        string realizationReference = imageReference ?? string.Empty;
        IContainerImageArtifact? indexedArtifact = null;
        IReadOnlyDictionary<string, string> archives =
            new Dictionary<string, string>(_options.ImageArchives, StringComparer.Ordinal);
        if (preparedIndex is string imageIndexPath)
        {
            IApplicationImageIndex index = await ContainerImageIndexes
                .ReadApplicationAsync(imageIndexPath, cancellationToken)
                .ConfigureAwait(false);
            ApplicationName manifestApplication = ApplicationName.Parse(
                manifestResource.Manifest.Application);
            if (index.Application != manifestApplication)
            {
                throw new InvalidDataException(
                    $"Image index '{imageIndexPath}' belongs to application '{index.Application}', not resource '{resource.Name}' application '{manifestApplication}'.");
            }

            IContainerImageIndexEntry entry = ContainerImageIndexes.Resolve(
                index,
                resource.Name,
                ArtifactRef.Self);
            IContainerImageArtifact? declared = imageReference is null ? null : ContainerImageArtifacts.Create(resource.Id, imageReference!);
            if (declared is not null && (!string.Equals(declared.Repository, entry.Repository, StringComparison.Ordinal)
                || !string.Equals(declared.Digest, entry.Digest, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Image index '{imageIndexPath}' entry for resource '{resource.Name}' declares '{entry.Repository}@{entry.Digest}', not manifest artifact.image '{declared.Repository}@{declared.Digest}'.");
            }

            string? archivePath = ContainerImageIndexes.ResolveArchivePath(imageIndexPath, entry);
            if (entry.Registry is null
                && _options.ContainerRegistry is null
                && archivePath is null)
            {
                throw new InvalidOperationException(
                    $"Image index '{imageIndexPath}' entry for resource '{resource.Name}' has a late-bound registry but DockerGatewayOptions.ContainerRegistry is absent and no archive is available.");
            }

            indexedArtifact = ContainerImageIndexes.CreateArtifact(
                resource.Id,
                entry,
                _options.ContainerRegistry);
            realizationReference = $"{indexedArtifact.Repository}@{indexedArtifact.Digest}";
            var indexedArchives = new Dictionary<string, string>(StringComparer.Ordinal);
            if (archivePath is not null)
            {
                indexedArchives.Add(realizationReference, archivePath);
            }

            archives = indexedArchives;
        }

        if (realizationReference.Length == 0)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' needs an ImageIndexPath or Local image publication to resolve ArtifactRef.Self.");
        }

        IDockerEngineClient engine = GetEngine();
        IImageRealizer realizer = _options.ImageRealizer
            ?? new DockerImageRealizer(engine, archives);
        IContainerImageArtifact? realized = await realizer
            .RealizeAsync(resource.Id, realizationReference, cancellationToken)
            .ConfigureAwait(false);
        if (realized is null)
        {
            throw new InvalidOperationException(
                $"The Docker image realizer returned no artifact for resource '{resource.Name}'.");
        }

        if (realized.Resource != resource.Id)
        {
            throw new InvalidOperationException(
                $"The Docker image realizer returned artifact '{realized.Resource}' while realizing '{resource.Id}'.");
        }

        IContainerImageArtifact validated = ContainerImageArtifacts.Create(
            resource.Id,
            $"{realized.Repository}@{realized.Digest}",
            realized.Tag);
        IContainerImageArtifact requiredArtifact = indexedArtifact
            ?? ContainerImageArtifacts.Create(resource.Id, imageReference!);
        if (!string.Equals(
                validated.Repository,
                requiredArtifact.Repository,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Digest,
                requiredArtifact.Digest,
                StringComparison.Ordinal))
        {
            string authority = indexedArtifact is null
                ? "manifest artifact"
                : "image-index artifact";
            throw new InvalidDataException(
                $"The Docker image realizer returned '{validated.Repository}@{validated.Digest}' for resource '{resource.Name}', not {authority} '{requiredArtifact.Repository}@{requiredArtifact.Digest}'.");
        }

        string canonicalReference = $"{validated.Repository}@{validated.Digest}";
        string inspectionReference = realized is DockerImageArtifact dockerArtifact
            ? dockerArtifact.ImageId
            : canonicalReference;
        DockerImageInspectResponse? inspection = await engine
            .InspectImageAsync(inspectionReference, cancellationToken)
            .ConfigureAwait(false);
        if (inspection is null)
        {
            throw new InvalidOperationException(
                $"The Docker image realizer did not make '{inspectionReference}' available in the selected engine for resource '{resource.Name}'.");
        }

        if (realized is DockerImageArtifact internalArtifact)
        {
            if (!string.Equals(
                inspection.Id,
                internalArtifact.ImageId,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Docker image '{inspectionReference}' for resource '{resource.Name}' resolved to image ID '{inspection.Id}', not verified image ID '{internalArtifact.ImageId}'.");
            }
        }
        else
        {
            DockerImageRealizer.RequireDigest(inspection, canonicalReference);
        }

        if (string.IsNullOrWhiteSpace(inspection.Id))
        {
            throw new InvalidDataException(
                $"Docker image '{canonicalReference}' has no immutable engine image ID.");
        }

        return new DockerImageArtifact(
            resource.Id,
            validated.Repository,
            validated.Digest,
            indexedArtifact is null ? validated.Tag : indexedArtifact.Tag,
            inspection.Id);
    }

    /// <inheritdoc/>
    protected override async Task StartObserverAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        _publishedIndexes.Clear();
        ArgumentNullException.ThrowIfNull(models);
        await GetEngine().PingAsync(cancellationToken).ConfigureAwait(false);
        _planController.BeginSession();
        _observer.Start();
    }

    /// <inheritdoc/>
    protected override async Task StopObserverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _observer.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _publishedIndexes.Clear();
            lock (_engineGate)
            {
                _engine?.Dispose();
                _engine = null;
            }
        }
    }

    /// <inheritdoc/>
    public string Render(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        ApplicationName application,
        string owner)
    {
        DockerPlanCompilation compilation = _compiler.Compile(
            plan,
            artifact,
            inputs,
            dependencies,
            application,
            owner,
            _options.PublicHost);
        ReportWarnings(compilation.Warnings);
        return DockerPlanRenderer.Render([compilation]);
    }

    /// <inheritdoc/>
    public async Task RenderAsync(
        IReadOnlyList<IApplicationModel> models,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var compilations = new List<DockerPlanCompilation>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            IApplicationModel model = models[modelIndex];
            for (int planIndex = 0; planIndex < model.Plans.Count; planIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResourcePlan plan = model.Plans[planIndex];
                IApplicationResource resource = FindRenderResource(model, plan.Resource);
                if (resource is IExternalResource)
                {
                    continue;
                }

                _compiler.Validate(plan);
                if (resource is not IManifestResource manifestResource)
                {
                    throw new InvalidDataException($"Docker render resource '{resource.Name}' has no manifest.");
                }

                IContainerImageArtifact artifact = await ResolveRenderArtifactAsync(
                    model, manifestResource, cancellationToken).ConfigureAwait(false);
                DockerPlanCompilation compilation = _compiler.Compile(
                    plan, artifact, ResourceInputs.Empty, [], model.Name, model.Owner,
                    _options.PublicHost, renderOnly: true);
                compilations.Add(compilation);
                for (int warningIndex = 0; warningIndex < compilation.Warnings.Count; warningIndex++)
                {
                    string warning = compilation.Warnings[warningIndex];
                    if (warnings.Add(warning))
                    {
                        _options.WarningHandler(warning);
                    }
                }
            }
        }

        await output.WriteAsync(DockerPlanRenderer.Render(compilations).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IContainerImageArtifact> ResolveRenderArtifactAsync(
        IApplicationModel model,
        IManifestResource resource,
        CancellationToken cancellationToken)
    {
        IContainerImageArtifact? declared = resource.Manifest.Artifact.Image is string image
            ? ContainerImageArtifacts.Create(resource.Id, image) : null;
        if (_options.ImageIndexPath is not string indexPath)
        {
            return declared ?? throw new InvalidDataException($"Resource '{resource.Name}' requires a pre-published ImageIndexPath for offline rendering.");
        }

        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            indexPath, cancellationToken).ConfigureAwait(false);
        if (index.Application != model.Name)
        {
            throw new InvalidDataException(
                $"Image index '{indexPath}' belongs to application '{index.Application}', not '{model.Name}'.");
        }

        IContainerImageIndexEntry entry = ContainerImageIndexes.Resolve(index, resource.Name, ArtifactRef.Self);
        if (declared is not null && (!string.Equals(entry.Repository, declared.Repository, StringComparison.Ordinal)
            || !string.Equals(entry.Digest, declared.Digest, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Image index '{indexPath}' entry for resource '{resource.Name}' differs from manifest artifact.image '{declared.Repository}@{declared.Digest}'.");
        }

        return ContainerImageIndexes.CreateArtifact(resource.Id, entry, _options.ContainerRegistry);
    }

    private static IApplicationResource FindRenderResource(IApplicationModel model, ResourceName name)
    {
        for (int index = 0; index < model.Resources.Count; index++)
        {
            IApplicationResource resource = model.Resources[index];
            if (resource.Name == name)
            {
                return resource;
            }
        }

        throw new InvalidDataException($"Docker render plan '{name}' has no matching resource.");
    }

    private void ReportWarnings(IReadOnlyList<string> warnings)
    {
        for (int index = 0; index < warnings.Count; index++)
        {
            _options.WarningHandler(warnings[index]);
        }
    }

    private IDockerEngineClient GetEngine()
    {
        lock (_engineGate)
        {
            return _engine ??= _engineFactory()
                ?? throw new InvalidOperationException("The Docker Engine client factory returned null.");
        }
    }

    private static Uri ResolveEngineEndpoint(DockerGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EngineEndpoint is not null)
        {
            return options.EngineEndpoint;
        }

        string? configured = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? endpoint))
            {
                throw new InvalidOperationException(
                    $"DOCKER_HOST value '{configured}' is not an absolute URI.");
            }

            return endpoint;
        }

        return OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine", UriKind.Absolute)
            : new Uri("unix:///var/run/docker.sock", UriKind.Absolute);
    }
}
