using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The gateway that realizes an <see cref="IApplicationModel"/> onto a Kubernetes cluster.
/// It compiles resource plans into native objects and reconciles them in owned namespaces.
/// </summary>
/// <remarks>
/// Normal stop preserves Kubernetes objects and persistent state. Teardown removes the compiled
/// object graph in reverse order and then removes the owned application namespace.
/// </remarks>
public sealed class KubernetesGateway : ApplicationGateway, IKubernetesManifestRenderer
{
    private readonly KubernetesGatewayOptions _options;
    private readonly InMemoryResourceStateManager _state = new();
    private readonly KubernetesPlanCompiler _compiler = new();
    private readonly KubernetesImageGatherer _imageGatherer;
    private readonly KubernetesPlanController _planController;
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;
    private readonly KubernetesGatewayObservationRegistry _observations;
    private readonly Dictionary<ApplicationName, NamespaceRegistration> _namespaces = new();
    private readonly List<ApplicationName> _namespaceOrder = [];
    private readonly Dictionary<ApplicationName, ExportRegistration> _exports = new();
    private readonly HashSet<ApplicationName> _teardownNamespaces = new();
    private readonly Dictionary<IApplicationResource, ImageGatherContext> _imageContexts =
        new(ReferenceEqualityComparer.Instance);

    private IKubernetes? _client;
    private bool _namespaceShutdownFailed;

    /// <summary>
    /// Initializes a new <see cref="KubernetesGateway"/> with default options.
    /// </summary>
    public KubernetesGateway()
        : this(new KubernetesGatewayOptions())
    {
    }

    /// <summary>
    /// Initializes a new <see cref="KubernetesGateway"/> with the given options.
    /// </summary>
    /// <param name="options">The options controlling cluster connection, apply, and teardown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A Kubernetes option is invalid.</exception>
    public KubernetesGateway(KubernetesGatewayOptions options)
        : this(options, CreateKindImageLoader(options))
    {
    }

    internal KubernetesGateway(
        KubernetesGatewayOptions options,
        IKindImageLoader kindImageLoader)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kindImageLoader);
        options.ValidateKubernetes();
        _options = options;
        _imageGatherer = new KubernetesImageGatherer(options, kindImageLoader);
        var resources = new KubernetesGatewayResourceApi(GetRequiredClient);
        KubernetesPlanController? planController = null;
        _observations = new KubernetesGatewayObservationRegistry(
            resources,
            MarkNamespaceForTeardown,
            RefreshApplicationExportAsync,
            options.ReadinessBudget,
            CanCommitBuiltInTeardown,
            (context, compilation, endpoints, cancellationToken) =>
                (planController ?? throw new InvalidOperationException(
                    "The Kubernetes plan controller has not been initialized."))
                .RefreshRuntimeEnvironmentAsync(
                    context,
                    compilation,
                    endpoints,
                    cancellationToken));
        planController = new KubernetesPlanController(
            options,
            _compiler,
            resources,
            _observations);
        _planController = planController;
        _controllers = [_planController];
    }

    /// <inheritdoc/>
    public override ResourceName Name => "kubernetes";

    /// <summary>
    /// The built-in platform controllers. Registered overrides from the common options are
    /// consulted first, followed by the generic Kubernetes plan controller.
    /// </summary>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State => _state;

    /// <inheritdoc/>
    protected override TimeSpan ReadinessBudget => _options.ReadinessBudget;

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

        if (descriptor.Resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be realized by the Kubernetes gateway because " +
                $"it does not expose an {nameof(IManifestResource)} manifest.");
        }

        string? image = manifestResource.Manifest.Artifact.Image;
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be realized by the Kubernetes gateway because " +
                "its manifest does not declare artifact.image.");
        }

        _ = ContainerImageArtifacts.Create(descriptor.Resource.Id, image);

        _imageContexts[descriptor.Resource] = new ImageGatherContext(
            model.Environment.IsDevelopment,
            plan.Container.Artifact);
    }

    /// <inheritdoc/>
    protected override async Task<IResourceArtifact> GatherAsync(
        IApplicationResource resource,
        CancellationToken cancellationToken)
    {
        if (!_imageContexts.TryGetValue(resource, out ImageGatherContext context))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no validated Kubernetes image-gather context.");
        }

        return await _imageGatherer
            .GatherAsync(
                resource,
                context.ArtifactReference,
                context.IsDevelopment,
                cancellationToken)
            .ConfigureAwait(false);
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
        KubernetesPlanCompilation compilation = _compiler.Compile(
            plan,
            artifact,
            inputs,
            dependencies,
            KubernetesMetadata.NamespaceName(application),
            owner,
            _options);
        for (int index = 0; index < compilation.Warnings.Count; index++)
        {
            _options.WarningHandler(compilation.Warnings[index]);
        }

        return KubernetesPlanRenderer.Render(compilation);
    }

    /// <inheritdoc/>
    protected override async Task PublishApplicationExportAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken)
    {
        NamespaceRegistration registration = FindNamespaceRegistration(document.Application);
        await registration.ExportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationExportDocument current = CreateApplicationExport(
                registration.Model,
                document.Version,
                document.TrustKey);
            await ApplyApplicationExportAsync(registration, current, cancellationToken)
                .ConfigureAwait(false);
            registration.LastExport = current;
        }
        finally
        {
            registration.ExportGate.Release();
        }
    }

    private async Task RefreshApplicationExportAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        NamespaceRegistration registration = FindNamespaceRegistration(model.Name.Value);
        await registration.ExportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationExportDocument? previous = registration.LastExport;
            if (previous is null)
            {
                return;
            }

            ApplicationExportDocument current = CreateApplicationExport(
                registration.Model,
                previous.Version,
                previous.TrustKey);
            await ApplyApplicationExportAsync(registration, current, cancellationToken)
                .ConfigureAwait(false);
            registration.LastExport = current;
        }
        finally
        {
            registration.ExportGate.Release();
        }
    }

    private async Task ApplyApplicationExportAsync(
        NamespaceRegistration registration,
        ApplicationExportDocument document,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        var export = new V1ConfigMap
        {
            ApiVersion = V1ConfigMap.KubeApiVersion,
            Kind = V1ConfigMap.KubeKind,
            Metadata = new V1ObjectMeta
            {
                Name = KubernetesMetadata.ExportName,
                NamespaceProperty = registration.NamespaceName,
                Labels = new Dictionary<string, string>
                {
                    [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue,
                },
                Annotations = new Dictionary<string, string>
                {
                    [KubernetesMetadata.OwnerAnnotation] = registration.Owner,
                },
            },
            Data = new Dictionary<string, string>
            {
                ["export.json"] = Encoding.UTF8.GetString(stream.ToArray()),
            },
        };
        var resources = new KubernetesResourceApi(GetRequiredClient(), registration.Owner);
        IKubernetesObject<V1ObjectMeta>? existing = await resources
            .ReadAsync(export, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireObjectOwnership(
                existing.Metadata,
                $"ConfigMap/{KubernetesMetadata.ExportName}",
                registration.Owner,
                registration.Model.Adopt);
            export.Metadata.ResourceVersion = existing.Metadata?.ResourceVersion;
        }

        if (existing is null)
        {
            if (await resources.TryCreateAsync(export, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            existing = await resources.ReadAsync(export, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    $"Kubernetes ConfigMap/{KubernetesMetadata.ExportName} was created and removed " +
                    "while export ownership was being checked. Retry publication.");
            }

            RequireObjectOwnership(
                existing.Metadata,
                $"ConfigMap/{KubernetesMetadata.ExportName}",
                registration.Owner,
                registration.Model.Adopt);
            throw new InvalidOperationException(
                $"Kubernetes ConfigMap/{KubernetesMetadata.ExportName} was created while its " +
                "absence was being checked. Retry publication against the latest object.");
        }

        await resources.ApplyAsync(export, force: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task RemoveApplicationExportAsync(
        ApplicationName application,
        CancellationToken cancellationToken)
    {
        if (_namespaceShutdownFailed)
        {
            throw new InvalidOperationException(
                "Kubernetes namespace shutdown did not complete; export withdrawal is deferred " +
                "so the gateway session can retry safely.");
        }

        if (!_exports.TryGetValue(application, out ExportRegistration registration))
        {
            return;
        }

        var desired = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = KubernetesMetadata.ExportName,
                NamespaceProperty = KubernetesMetadata.NamespaceName(application),
            },
        };
        var resources = new KubernetesResourceApi(GetRequiredClient(), registration.Owner);
        IKubernetesObject<V1ObjectMeta>? existing = await resources
            .ReadAsync(desired, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireObjectOwnership(
                existing.Metadata,
                $"ConfigMap/{KubernetesMetadata.ExportName}",
                registration.Owner,
                registration.Adopt);
            await resources.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        _exports.Remove(application);
        DisposeClusterSessionIfComplete();
    }

    /// <inheritdoc/>
    protected override async Task StartObserverAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (_client is not null && _exports.Count == 0 && !_namespaceShutdownFailed)
        {
            DisposeClusterSessionIfComplete();
        }

        if (_client is not null || _namespaces.Count != 0 || _exports.Count != 0)
        {
            throw new InvalidOperationException(
                "The Kubernetes observer is already connected to a gateway session.");
        }

        ValidateNormalizedNames(models);
        _planController.BeginSession();

        IKubernetes client = KubernetesClientFactory.Create(_options);
        try
        {
            for (int index = 0; index < models.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IApplicationModel model = models[index];
                string namespaceName = KubernetesMetadata.NamespaceName(model.Name);
                var manager = new KubernetesNamespaceManager(client, model.Owner);
                await manager
                    .EnsureAsync(namespaceName, model.Owner, model.Adopt, cancellationToken)
                    .ConfigureAwait(false);
                _namespaces.Add(
                    model.Name,
                    new NamespaceRegistration(namespaceName, model.Owner, model));
                _namespaceOrder.Add(model.Name);
                _exports.Add(model.Name, new ExportRegistration(model.Owner, model.Adopt));
            }

            _client = client;
            for (int index = 0; index < models.Count; index++)
            {
                IApplicationModel model = models[index];
                await _planController
                    .PruneRemovedResourcesAsync(
                        model,
                        KubernetesMetadata.NamespaceName(model.Name),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _observations.Start();
        }
        catch
        {
            _client = null;
            ClearNamespaceRegistrations();
            _exports.Clear();
            _teardownNamespaces.Clear();
            _namespaceShutdownFailed = false;
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override async Task StopObserverAsync(CancellationToken cancellationToken)
    {
        IKubernetes? client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            await _observations.StopAsync().ConfigureAwait(false);
            Exception? failure = null;
            for (int index = _namespaceOrder.Count - 1; index >= 0; index--)
            {
                ApplicationName application = _namespaceOrder[index];
                if (!_teardownNamespaces.Contains(application))
                {
                    continue;
                }

                if (!_namespaces.TryGetValue(application, out NamespaceRegistration? registration))
                {
                    continue;
                }

                var manager = new KubernetesNamespaceManager(client, registration.Owner);
                try
                {
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    grace.CancelAfter(_options.StopGrace);
                    await manager
                        .DeleteAsync(registration.NamespaceName, registration.Owner, grace.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            if (failure is not null)
            {
                _namespaceShutdownFailed = true;
                throw new InvalidOperationException(
                    "The Kubernetes gateway could not delete every namespace marked for teardown.",
                    failure);
            }

            _namespaceShutdownFailed = false;
            _teardownNamespaces.Clear();
            DisposeClusterSessionIfComplete();
        }
        catch
        {
            _namespaceShutdownFailed = true;
            throw;
        }
    }

    /// <summary>
    /// Marks an application's owned namespace for deletion after controller teardown. Normal
    /// observer stop never calls this method and therefore preserves cluster state.
    /// </summary>
    internal void MarkNamespaceForTeardown(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_namespaces.ContainsKey(model.Name))
        {
            throw new InvalidOperationException(
                $"Application '{model.Name}' has no active Kubernetes namespace session.");
        }

        _teardownNamespaces.Add(model.Name);
    }

    private bool CanCommitBuiltInTeardown(IApplicationModel model)
    {
        for (int descriptorIndex = 0; descriptorIndex < model.Descriptors.Count; descriptorIndex++)
        {
            IApplicationResourceDescriptor descriptor = model.Descriptors[descriptorIndex];
            if (descriptor.Resource is IExternalResource || descriptor.Plan is not ResourcePlan plan)
            {
                continue;
            }

            for (int controllerIndex = 0; controllerIndex < _options.Controllers.Count; controllerIndex++)
            {
                if (_options.Controllers[controllerIndex].CanRealize(plan, out _))
                {
                    // The base lifecycle does not expose this controller's delete outcome to the
                    // Kubernetes observer. Preserve the namespace even if all built-in resources
                    // were removed, rather than erase state after an unobservable custom failure.
                    return false;
                }
            }
        }

        return true;
    }

    internal IKubernetes GetRequiredClient() =>
        _client ?? throw new InvalidOperationException(
            "The Kubernetes gateway has not started its cluster session.");

    internal static void ValidateNormalizedNames(IReadOnlyList<IApplicationModel> models)
    {
        var namespaces = new Dictionary<string, ApplicationName>(StringComparer.Ordinal);
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            IApplicationModel model = models[modelIndex];
            string namespaceName = KubernetesMetadata.NamespaceName(model.Name);
            if (namespaces.TryGetValue(namespaceName, out ApplicationName existingApplication))
            {
                throw new InvalidOperationException(
                    $"Applications '{existingApplication}' and '{model.Name}' both normalize to " +
                    $"Kubernetes namespace '{namespaceName}'. Kubernetes application names must be " +
                    "unique after lowercase normalization.");
            }

            namespaces.Add(namespaceName, model.Name);
            var resources = new Dictionary<string, ResourceName>(StringComparer.Ordinal);
            var services = new Dictionary<string, ResourceName>(StringComparer.Ordinal);
            var standaloneClaims = new Dictionary<string, ResourceName>(StringComparer.Ordinal);
            var statefulClaimPrefixes = new Dictionary<string, ResourceName>(StringComparer.Ordinal);
            for (int descriptorIndex = 0; descriptorIndex < model.Descriptors.Count; descriptorIndex++)
            {
                IApplicationResourceDescriptor descriptor = model.Descriptors[descriptorIndex];
                IApplicationResource resource = descriptor.Resource;
                if (resource is IExternalResource)
                {
                    continue;
                }

                string resourceName = KubernetesMetadata.ResourceName(resource.Name);
                if (resources.TryGetValue(resourceName, out ResourceName existingResource))
                {
                    throw new InvalidOperationException(
                        $"Resources '{existingResource}' and '{resource.Name}' in application " +
                        $"'{model.Name}' both normalize to Kubernetes resource name '{resourceName}'. " +
                        "Kubernetes resource names must be unique after lowercase normalization.");
                }

                resources.Add(resourceName, resource.Name);
                ResourcePlan? plan = descriptor.Plan;
                if (plan is null)
                {
                    continue;
                }

                for (int serviceIndex = 0; serviceIndex < plan.Services.Count; serviceIndex++)
                {
                    string serviceName = plan.Services[serviceIndex].Name;
                    if (services.TryGetValue(serviceName, out ResourceName existingServiceResource))
                    {
                        throw new InvalidOperationException(
                            $"Resources '{existingServiceResource}' and '{resource.Name}' in " +
                            $"application '{model.Name}' both compile Kubernetes Service/" +
                            $"{serviceName}. Service names must be unique within a namespace.");
                    }

                    services.Add(serviceName, resource.Name);
                }

                for (int volumeIndex = 0; volumeIndex < plan.Volumes.Count; volumeIndex++)
                {
                    string volumeName = plan.Volumes[volumeIndex].Name;
                    if (plan.Workload.Kind is WorkloadKind.StatefulSet)
                    {
                        string prefix = $"{volumeName}-{resourceName}";
                        if (statefulClaimPrefixes.TryGetValue(
                                prefix,
                                out ResourceName existingPrefixResource))
                        {
                            throw new InvalidOperationException(
                                $"Resources '{existingPrefixResource}' and '{resource.Name}' in " +
                                $"application '{model.Name}' both compile StatefulSet claim prefix " +
                                $"'{prefix}'. Claim prefixes must be unique within a namespace.");
                        }

                        statefulClaimPrefixes.Add(prefix, resource.Name);
                    }
                    else
                    {
                        if (standaloneClaims.TryGetValue(
                                volumeName,
                                out ResourceName existingClaimResource))
                        {
                            throw new InvalidOperationException(
                                $"Resources '{existingClaimResource}' and '{resource.Name}' in " +
                                $"application '{model.Name}' both compile PersistentVolumeClaim/" +
                                $"{volumeName}. Claim names must be unique within a namespace.");
                        }

                        standaloneClaims.Add(volumeName, resource.Name);
                    }
                }
            }

            foreach ((string claimName, ResourceName claimResource) in standaloneClaims)
            {
                foreach ((string prefix, ResourceName prefixResource) in statefulClaimPrefixes)
                {
                    if (IsGeneratedClaimName(claimName, prefix))
                    {
                        throw new InvalidOperationException(
                            $"Resources '{claimResource}' and '{prefixResource}' in application " +
                            $"'{model.Name}' can both compile PersistentVolumeClaim/{claimName}. " +
                            "Standalone claim names must not overlap StatefulSet claim prefixes.");
                    }
                }
            }
        }
    }

    private static bool IsGeneratedClaimName(string claimName, string prefix)
    {
        if (!claimName.StartsWith(prefix, StringComparison.Ordinal)
            || claimName.Length <= prefix.Length + 1
            || claimName[prefix.Length] != '-')
        {
            return false;
        }

        for (int index = prefix.Length + 1; index < claimName.Length; index++)
        {
            if (claimName[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private ApplicationExportDocument CreateApplicationExport(
        IApplicationModel model,
        string version,
        JsonElement? trustKey)
    {
        IApplicationResourceStateManager state = GetApplicationState(model);
        var endpoints = new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>();
        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            if (model.Descriptors[index].Resource is IExternalResource)
            {
                continue;
            }

            IApplicationResource resource = model.Descriptors[index].Resource;
            IReadOnlyList<ApplicationExportEndpoint> observed = CreateExportEndpoints(
                state.GetObservedEndpoints(resource.Id),
                model.Manifests[index]);
            if (observed.Count != 0)
            {
                endpoints.Add(resource.Name, observed);
            }
        }

        return ApplicationExportDocument.Create(model, version, endpoints, trustKey);
    }

    private NamespaceRegistration FindNamespaceRegistration(string application)
    {
        foreach ((ApplicationName name, NamespaceRegistration registration) in _namespaces)
        {
            if (string.Equals(name.Value, application, StringComparison.Ordinal))
            {
                return registration;
            }
        }

        throw new InvalidOperationException(
            $"Application '{application}' has no active Kubernetes namespace.");
    }

    private void ClearNamespaceRegistrations()
    {
        foreach (NamespaceRegistration registration in _namespaces.Values)
        {
            registration.ExportGate.Dispose();
        }

        _namespaces.Clear();
        _namespaceOrder.Clear();
    }

    private void DisposeClusterSessionIfComplete()
    {
        if (_exports.Count != 0 || _namespaceShutdownFailed || _client is not IKubernetes client)
        {
            return;
        }

        _client = null;
        ClearNamespaceRegistrations();
        _teardownNamespaces.Clear();
        client.Dispose();
    }

    internal static IReadOnlyList<ApplicationExportEndpoint> CreateExportEndpoints(
        IReadOnlyList<ResourceEndpoint> observed,
        ResourceManifest manifest)
    {
        var order = new List<string>();
        var addresses = new Dictionary<string, ExportEndpointPair>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            declared.Add(manifest.Endpoints[index].Name);
        }

        for (int index = 0; index < observed.Count; index++)
        {
            ResourceEndpoint endpoint = observed[index];
            if (!declared.Contains(endpoint.Name) ||
                string.IsNullOrWhiteSpace(endpoint.Scheme) ||
                string.IsNullOrWhiteSpace(endpoint.Host) ||
                endpoint.Port <= 0)
            {
                continue;
            }

            string internalAddress = CreateAuthority(endpoint.Host, endpoint.Port);
            string? publicAddress = endpoint.IsPublic ? CreateAbsoluteAddress(endpoint) : null;
            if (!addresses.TryGetValue(endpoint.Name, out ExportEndpointPair current))
            {
                order.Add(endpoint.Name);
                addresses.Add(
                    endpoint.Name,
                    new ExportEndpointPair(internalAddress, publicAddress, !endpoint.IsPublic));
                continue;
            }

            if (!endpoint.IsPublic && !current.HasInternalObservation)
            {
                current = current with
                {
                    Internal = internalAddress,
                    HasInternalObservation = true,
                };
            }

            if (endpoint.IsPublic && current.Public is null)
            {
                current = current with { Public = publicAddress };
            }

            addresses[endpoint.Name] = current;
        }

        var exported = new ApplicationExportEndpoint[order.Count];
        for (int index = 0; index < exported.Length; index++)
        {
            ExportEndpointPair address = addresses[order[index]];
            exported[index] = new ApplicationExportEndpoint(
                order[index],
                address.Internal,
                address.Public);
        }

        return exported;
    }

    private static string CreateAuthority(string host, int port)
    {
        string formattedHost = host.Contains(":", StringComparison.Ordinal) &&
            !host.StartsWith("[", StringComparison.Ordinal)
                ? $"[{host}]"
                : host;
        return $"{formattedHost}:{port}";
    }

    private static string CreateAbsoluteAddress(ResourceEndpoint endpoint) =>
        Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host!, endpoint.Port).ToEndpointString();

    private static IKindImageLoader CreateKindImageLoader(KubernetesGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new KindImageLoader(
            options,
            new KubernetesContextResolver(),
            new KindCommandRunner());
    }

    private static void RequireObjectOwnership(
        V1ObjectMeta? metadata,
        string description,
        string owner,
        bool adopt)
    {
        string? existingOwner = null;
        _ = metadata?.Annotations?.TryGetValue(
            KubernetesMetadata.OwnerAnnotation,
            out existingOwner);
        if (string.Equals(existingOwner, owner, StringComparison.Ordinal) || adopt)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Kubernetes object '{description}' is owned by '{existingOwner ?? "<none>"}', " +
            $"not '{owner}'. Pass --adopt to take ownership.");
    }

    private readonly record struct ExportEndpointPair(
        string Internal,
        string? Public,
        bool HasInternalObservation);

    private readonly record struct ExportRegistration(string Owner, bool Adopt);

    private readonly record struct ImageGatherContext(
        bool IsDevelopment,
        ArtifactRef ArtifactReference);

    private sealed class NamespaceRegistration : IDisposable
    {
        public NamespaceRegistration(string namespaceName, string owner, IApplicationModel model)
        {
            NamespaceName = namespaceName;
            Owner = owner;
            Model = model;
        }

        public string NamespaceName { get; }

        public string Owner { get; }

        public IApplicationModel Model { get; }

        public SemaphoreSlim ExportGate { get; } = new(1, 1);

        public ApplicationExportDocument? LastExport { get; set; }

        public void Dispose() => ExportGate.Dispose();
    }
}
