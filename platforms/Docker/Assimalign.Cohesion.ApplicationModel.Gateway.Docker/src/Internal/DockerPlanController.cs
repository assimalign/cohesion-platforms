using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerPlanController : IApplicationResourceController
{
    private readonly DockerGatewayOptions _options;
    private readonly DockerPlanCompiler _compiler;
    private readonly Func<IDockerEngineClient> _getEngine;
    private readonly DockerContainerObserver _observer;
    private readonly Func<IResourceControlContext, CancellationToken, ValueTask<ResourceRecord?>>? _readRecord;
    private readonly HashSet<string> _reportedWarnings = new(StringComparer.Ordinal);
    // The record each container last received, keyed by container name, so a later copy knows which layers to
    // withdraw. Lost on a gateway restart, after which only layers the next record still has are rewritten.
    private readonly Dictionary<string, ResourceRecord> _delivered = new(StringComparer.Ordinal);

    public DockerPlanController(
        DockerGatewayOptions options,
        DockerPlanCompiler compiler,
        Func<IDockerEngineClient> getEngine,
        DockerContainerObserver observer,
        Func<IResourceControlContext, CancellationToken, ValueTask<ResourceRecord?>>? readRecord = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(getEngine);
        ArgumentNullException.ThrowIfNull(observer);
        _options = options;
        _compiler = compiler;
        _getEngine = getEngine;
        _observer = observer;
        _readRecord = readRecord;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            _compiler.Validate(plan);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            reason = exception.Message;
            return false;
        }

        reason = null;
        return true;
    }

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IContainerImageArtifact artifact = context.GetArtifact<IContainerImageArtifact>();
        if (artifact.Resource != context.Resource.Id)
        {
            throw new InvalidOperationException(
                $"Docker image artifact '{artifact.Resource}' does not belong to resource '{context.Resource.Id}'.");
        }

        DockerPlanCompilation compilation = _compiler.Compile(
            context.Plan,
            artifact,
            context.Inputs,
            context.ObservedDependencies,
            context.Model.Name,
            context.Model.Owner,
            _options.PublicHost);
        ReportWarnings(compilation.Warnings);
        ResourceRecord? record = await ReadRecordAsync(context, cancellationToken).ConfigureAwait(false);

        string containerId;
        using (await _observer.EnterMutationAsync(context, cancellationToken).ConfigureAwait(false))
        {
            IDockerEngineClient engine = _getEngine();
            await EnsureNetworkAsync(engine, compilation, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < compilation.Volumes.Count; index++)
            {
                await EnsureVolumeAsync(engine, compilation.Volumes[index], cancellationToken)
                    .ConfigureAwait(false);
            }

            DockerContainerInspectResponse? existing = await engine
                .InspectContainerAsync(compilation.Container.Name, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                RequireContainerOwnership(existing, compilation);
                if (!ContainerMatches(existing, compilation))
                {
                    _observer.Unregister(context);
                    await ReplaceContainerAsync(engine, existing, compilation, cancellationToken)
                        .ConfigureAwait(false);
                    containerId = await CreateAndStartAsync(engine, compilation, RecordFiles(compilation, record, fresh: true), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    containerId = RequireId(existing, compilation.Container.Name);
                    if (existing.State?.Running is not true
                        && compilation.Container.Workload is not WorkloadKind.Job)
                    {
                        await StageInputsAsync(
                            engine,
                            containerId,
                            compilation,
                            sensitive: false,
                            cancellationToken).ConfigureAwait(false);
                        await StageFilesAsync(engine, containerId, RecordFiles(compilation, record, fresh: false), cancellationToken)
                            .ConfigureAwait(false);
                        await engine.StartContainerAsync(containerId, cancellationToken)
                            .ConfigureAwait(false);
                        await StageInputsAsync(
                            engine,
                            containerId,
                            compilation,
                            sensitive: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (existing.State?.Running is true)
                    {
                        // A change the gateway could not copy while it was made reaches the container now.
                        await StageFilesAsync(engine, containerId, RecordFiles(compilation, record, fresh: false), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            else
            {
                _observer.Unregister(context);
                containerId = await CreateAndStartAsync(engine, compilation, RecordFiles(compilation, record, fresh: true), cancellationToken)
                    .ConfigureAwait(false);
            }

            Remember(compilation, record);
        }

        RestartPolicy restartPolicy = ReadRestartPolicy(context.Plan, context.Resource);
        await _observer
            .RegisterAsync(context, compilation, containerId, restartPolicy, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _observer.BeginStop(context);
        using (await _observer.EnterMutationAsync(context, cancellationToken).ConfigureAwait(false))
        {
            IDockerEngineClient engine = _getEngine();
            DockerContainerInspectResponse? existing = await engine
                .InspectContainerAsync(
                    DockerMetadata.ContainerName(context.Model.Name, context.Plan.Resource),
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                RequireResourceOwnership(existing.Config?.Labels, context, "container");
                await engine
                    .StopContainerAsync(
                        RequireId(existing, existing.Name ?? context.Plan.Resource.ToString()),
                        context.Plan.Workload.StopGraceSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _observer.PublishStopped(context, "Docker container stopped gracefully; named volumes were retained.");
        _observer.Unregister(context);
    }

    public async Task DeleteAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _observer.BeginStop(context);
        IDockerEngineClient engine = _getEngine();
        ExceptionDispatchInfo? firstFailure = null;
        string containerName = DockerMetadata.ContainerName(context.Model.Name, context.Plan.Resource);
        using (await _observer.EnterMutationAsync(context, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                DockerContainerInspectResponse? existing = await engine
                    .InspectContainerAsync(containerName, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    RequireResourceOwnership(existing.Config?.Labels, context, "container");
                    string id = RequireId(existing, containerName);
                    await engine
                        .StopContainerAsync(id, context.Plan.Workload.StopGraceSeconds, cancellationToken)
                        .ConfigureAwait(false);
                    await engine
                        .RemoveContainerAsync(id, force: false, removeVolumes: false, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstFailure = ExceptionDispatchInfo.Capture(exception);
            }

            for (int index = context.Plan.Volumes.Count - 1; index >= 0; index--)
            {
                string volumeName = DockerMetadata.VolumeName(
                    context.Model.Name,
                    context.Plan.Resource,
                    context.Plan.Volumes[index].Name);
                try
                {
                    DockerVolumeInspectResponse? volume = await engine
                        .InspectVolumeAsync(volumeName, cancellationToken)
                        .ConfigureAwait(false);
                    if (volume is not null)
                    {
                        RequireResourceOwnership(volume.Labels, context, "volume");
                        await engine.RemoveVolumeAsync(volumeName, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            try
            {
                await RemoveNetworkIfUnusedAsync(engine, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        if (firstFailure is not null)
        {
            _observer.PublishFailed(context, "Docker teardown did not remove every owned object.");
            _observer.Unregister(context);
            firstFailure.Throw();
        }

        _observer.PublishStopped(context, "Docker container, network membership, and named volumes were removed.");
        _observer.Unregister(context);
    }

    public void BeginSession()
    {
        lock (_reportedWarnings)
        {
            _reportedWarnings.Clear();
        }
    }

    public async Task<string> RestartAsync(
        IResourceControlContext context,
        DockerPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        IDockerEngineClient engine = _getEngine();
        ResourceRecord? record = await ReadRecordAsync(context, cancellationToken).ConfigureAwait(false);
        DockerContainerInspectResponse? existing = await engine
            .InspectContainerAsync(compilation.Container.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireContainerOwnership(existing, compilation);
            await ReplaceContainerAsync(engine, existing, compilation, cancellationToken)
                .ConfigureAwait(false);
        }

        string id = await CreateAndStartAsync(engine, compilation, RecordFiles(compilation, record, fresh: true), cancellationToken)
            .ConfigureAwait(false);
        Remember(compilation, record);
        return id;
    }

    // Copies a management change into the resource's running container, which polls its record directory. A stopped
    // or missing container needs nothing: the record is staged whenever a container starts.
    internal async Task DeliverRecordAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord previous,
        ResourceRecord current,
        CancellationToken cancellationToken)
    {
        string name = DockerMetadata.ContainerName(application, resource);
        IDockerEngineClient engine = _getEngine();
        DockerContainerInspectResponse? container = await engine.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false);
        if (container?.State?.Running is not true || string.IsNullOrWhiteSpace(container.Id))
        {
            return;
        }

        ResourceRecord? delivered;
        lock (_delivered)
        {
            delivered = _delivered.TryGetValue(name, out ResourceRecord? known) ? known : previous;
        }

        await StageFilesAsync(engine, container.Id, DockerResourceRecordStore.CreateFiles(current, delivered), cancellationToken)
            .ConfigureAwait(false);
        lock (_delivered)
        {
            _delivered[name] = current;
        }
    }

    private async ValueTask<ResourceRecord?> ReadRecordAsync(IResourceControlContext context, CancellationToken cancellationToken) =>
        _readRecord is not null && context.Inputs.ConfigurationDirectory is not null
            ? await _readRecord(context, cancellationToken).ConfigureAwait(false)
            : null;

    // A fresh container holds nothing, so only the record's layers are written; an existing one also receives an
    // empty object for each layer the record it last received had and this one does not.
    private IReadOnlyList<DockerFilePlan> RecordFiles(DockerPlanCompilation compilation, ResourceRecord? record, bool fresh)
    {
        if (record is null)
        {
            return Array.Empty<DockerFilePlan>();
        }

        ResourceRecord? delivered = null;
        if (!fresh)
        {
            lock (_delivered)
            {
                _delivered.TryGetValue(compilation.Container.Name, out delivered);
            }
        }

        return DockerResourceRecordStore.CreateFiles(record, delivered);
    }

    private void Remember(DockerPlanCompilation compilation, ResourceRecord? record)
    {
        if (record is null)
        {
            return;
        }

        lock (_delivered)
        {
            _delivered[compilation.Container.Name] = record;
        }
    }

    private static async Task StageFilesAsync(
        IDockerEngineClient engine,
        string containerId,
        IReadOnlyList<DockerFilePlan> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        using MemoryStream archive = DockerInputArchive.Create(files);
        await engine
            .PutArchiveAsync(containerId, "/", archive, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    internal static RestartPolicy ReadRestartPolicy(ResourcePlan plan, IApplicationResource resource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(resource);
        string value = plan.Workload.RestartPolicy;
        if (string.IsNullOrEmpty(value) && resource is IManifestResource manifestResource)
        {
            value = manifestResource.Manifest.Lifecycle.RestartPolicy;
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Docker resource '{resource.Name}' does not expose a manifest restart policy.");
        }

        if (!Enum.TryParse(value, ignoreCase: true, out RestartPolicy policy)
            || !Enum.IsDefined(policy))
        {
            throw new InvalidDataException(
                $"Docker resource '{resource.Name}' declares unsupported restart policy '{value}'.");
        }

        return policy;
    }

    private static async Task EnsureNetworkAsync(
        IDockerEngineClient engine,
        DockerPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        DockerNetworkInspectResponse? existing = await engine
            .InspectNetworkAsync(compilation.Network.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireLabels(
                existing.Labels,
                compilation.Network.Labels,
                $"Docker network '{compilation.Network.Name}'");
            return;
        }

        _ = await engine.CreateNetworkAsync(
            new DockerNetworkCreateRequest
            {
                Name = compilation.Network.Name,
                CheckDuplicate = true,
                Driver = "bridge",
                Attachable = true,
                Labels = Copy(compilation.Network.Labels),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureVolumeAsync(
        IDockerEngineClient engine,
        DockerVolumePlan volume,
        CancellationToken cancellationToken)
    {
        DockerVolumeInspectResponse? existing = await engine
            .InspectVolumeAsync(volume.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireLabels(
                existing.Labels,
                RequiredOwnershipLabels(volume.Labels),
                $"Docker volume '{volume.Name}'");
            return;
        }

        _ = await engine.CreateVolumeAsync(
            new DockerVolumeCreateRequest
            {
                Name = volume.Name,
                Driver = "local",
                Labels = Copy(volume.Labels),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CreateAndStartAsync(
        IDockerEngineClient engine,
        DockerPlanCompilation compilation,
        IReadOnlyList<DockerFilePlan> recordFiles,
        CancellationToken cancellationToken)
    {
        DockerContainerCreateResponse created = await engine
            .CreateContainerAsync(
                compilation.Container.Name,
                CreateContainerRequest(compilation),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidDataException(
                $"Docker created container '{compilation.Container.Name}' without returning an ID.");
        }

        await StageInputsAsync(
            engine,
            created.Id,
            compilation,
            sensitive: false,
            cancellationToken).ConfigureAwait(false);
        // The record is in place before the process starts, so its startup phase applies it.
        await StageFilesAsync(engine, created.Id, recordFiles, cancellationToken).ConfigureAwait(false);
        await engine.StartContainerAsync(created.Id, cancellationToken).ConfigureAwait(false);
        await StageInputsAsync(
            engine,
            created.Id,
            compilation,
            sensitive: true,
            cancellationToken)
            .ConfigureAwait(false);
        return created.Id;
    }

    private static async Task StageInputsAsync(
        IDockerEngineClient engine,
        string containerId,
        DockerPlanCompilation compilation,
        bool sensitive,
        CancellationToken cancellationToken)
    {
        var files = new List<DockerFilePlan>();
        for (int index = 0; index < compilation.Files.Count; index++)
        {
            DockerFilePlan file = compilation.Files[index];
            if (file.Sensitive == sensitive)
            {
                files.Add(file);
            }
        }

        if (files.Count == 0)
        {
            return;
        }

        using MemoryStream archive = DockerInputArchive.Create(files);
        await engine
            .PutArchiveAsync(containerId, "/", archive, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ReplaceContainerAsync(
        IDockerEngineClient engine,
        DockerContainerInspectResponse existing,
        DockerPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        string id = RequireId(existing, compilation.Container.Name);
        await engine
            .StopContainerAsync(id, compilation.Container.StopGraceSeconds, cancellationToken)
            .ConfigureAwait(false);
        await engine
            .RemoveContainerAsync(id, force: false, removeVolumes: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private static DockerContainerCreateRequest CreateContainerRequest(
        DockerPlanCompilation compilation)
    {
        DockerContainerPlan container = compilation.Container;
        var exposedPorts = new Dictionary<string, DockerEmptyObject>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, List<DockerPortBinding>>(StringComparer.Ordinal);
        for (int index = 0; index < container.PortBindings.Count; index++)
        {
            DockerPortPublishPlan port = container.PortBindings[index];
            string key = port.ContainerPort.ToString(CultureInfo.InvariantCulture)
                + "/" + port.Protocol;
            exposedPorts.TryAdd(key, new DockerEmptyObject());
            if (!bindings.TryGetValue(key, out List<DockerPortBinding>? values))
            {
                values = [];
                bindings.Add(key, values);
            }

            values.Add(new DockerPortBinding
            {
                HostIp = port.HostIp,
                HostPort = port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            });
        }

        var portBindings = new Dictionary<string, DockerPortBinding[]?>(StringComparer.Ordinal);
        foreach ((string key, List<DockerPortBinding> values) in bindings)
        {
            portBindings.Add(key, [.. values]);
        }

        var mounts = new DockerMountRequest[container.VolumeMounts.Count];
        for (int index = 0; index < mounts.Length; index++)
        {
            mounts[index] = new DockerMountRequest
            {
                Type = "volume",
                Source = container.VolumeMounts[index].Source,
                Target = container.VolumeMounts[index].Target,
            };
        }

        var tmpfs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < container.Tmpfs.Count; index++)
        {
            tmpfs[container.Tmpfs[index]] = "rw,noexec,nosuid,nodev,mode=0700";
        }

        var environmentKeys = new List<string>(container.Environment.Keys);
        environmentKeys.Sort(StringComparer.Ordinal);
        var environment = new string[environmentKeys.Count];
        for (int index = 0; index < environment.Length; index++)
        {
            string key = environmentKeys[index];
            environment[index] = key + "=" + container.Environment[key];
        }

        return new DockerContainerCreateRequest
        {
            Image = container.Image,
            Env = environment,
            ExposedPorts = exposedPorts.Count == 0 ? null : exposedPorts,
            Labels = Copy(container.Labels),
            StopTimeout = container.StopGraceSeconds,
            HostConfig = new DockerHostConfig
            {
                NetworkMode = compilation.Network.Name,
                PortBindings = portBindings.Count == 0 ? null : portBindings,
                RestartPolicy = new DockerRestartPolicy
                {
                    Name = "no",
                    MaximumRetryCount = 0,
                },
                AutoRemove = false,
                Mounts = mounts.Length == 0 ? null : mounts,
                Tmpfs = tmpfs.Count == 0 ? null : tmpfs,
            },
            NetworkingConfig = new DockerNetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, DockerEndpointSettings>(StringComparer.Ordinal)
                {
                    [compilation.Network.Name] = new DockerEndpointSettings
                    {
                        Aliases = [.. container.NetworkAliases],
                    },
                },
            },
        };
    }

    private static bool ContainerMatches(
        DockerContainerInspectResponse existing,
        DockerPlanCompilation compilation)
    {
        Dictionary<string, string>? labels = existing.Config?.Labels;
        return labels is not null
            && labels.TryGetValue(DockerMetadata.PlanHashLabel, out string? planHash)
            && string.Equals(planHash, compilation.PlanHash, StringComparison.Ordinal)
            && labels.TryGetValue(DockerMetadata.RuntimeHashLabel, out string? runtimeHash)
            && string.Equals(runtimeHash, compilation.RuntimeHash, StringComparison.Ordinal)
            && string.Equals(existing.Image, compilation.Container.Image, StringComparison.Ordinal);
    }

    private static void RequireContainerOwnership(
        DockerContainerInspectResponse existing,
        DockerPlanCompilation compilation)
    {
        RequireLabels(
            existing.Config?.Labels,
            RequiredOwnershipLabels(compilation.Container.Labels),
            $"Docker container '{compilation.Container.Name}'");
    }

    private static void RequireResourceOwnership(
        IReadOnlyDictionary<string, string>? labels,
        IResourceControlContext context,
        string kind)
    {
        var required = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerMetadata.ApplicationLabel] = context.Model.Name.ToString(),
            [DockerMetadata.OwnerLabel] = context.Model.Owner,
            [DockerMetadata.ResourceLabel] = context.Plan.Resource.ToString(),
        };
        RequireLabels(
            labels,
            required,
            $"Docker {kind} for resource '{context.Plan.Resource}'");
    }

    private static IReadOnlyDictionary<string, string> RequiredOwnershipLabels(
        IReadOnlyDictionary<string, string> labels)
    {
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] keys =
        [
            DockerMetadata.ApplicationLabel,
            DockerMetadata.OwnerLabel,
            DockerMetadata.ResourceLabel,
        ];
        for (int index = 0; index < keys.Length; index++)
        {
            if (labels.TryGetValue(keys[index], out string? value))
            {
                required.Add(keys[index], value);
            }
        }

        return required;
    }

    private static void RequireLabels(
        IReadOnlyDictionary<string, string>? observed,
        IReadOnlyDictionary<string, string> required,
        string description)
    {
        foreach ((string key, string value) in required)
        {
            if (observed is null
                || !observed.TryGetValue(key, out string? observedValue)
                || !string.Equals(observedValue, value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{description} is not owned by this gateway: label '{key}' must equal '{value}'.");
            }
        }
    }

    private async Task RemoveNetworkIfUnusedAsync(
        IDockerEngineClient engine,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        if (!MayDeleteSharedNetwork(context.Model))
        {
            return;
        }

        string networkName = DockerMetadata.ApplicationNetwork(context.Model.Name);
        DockerNetworkInspectResponse? network = await engine
            .InspectNetworkAsync(networkName, cancellationToken)
            .ConfigureAwait(false);
        if (network is null)
        {
            return;
        }

        var required = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerMetadata.ApplicationLabel] = context.Model.Name.ToString(),
            [DockerMetadata.OwnerLabel] = context.Model.Owner,
        };
        RequireLabels(network.Labels, required, $"Docker network '{networkName}'");
        if (network.Containers is { Count: > 0 })
        {
            return;
        }

        await engine.RemoveNetworkAsync(network.Id ?? networkName, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool MayDeleteSharedNetwork(IApplicationModel model)
    {
        if (_options.Controllers.Count == 0)
        {
            return true;
        }

        for (int descriptorIndex = 0; descriptorIndex < model.Descriptors.Count; descriptorIndex++)
        {
            ResourcePlan? plan = model.Descriptors[descriptorIndex].Plan;
            if (plan is null)
            {
                continue;
            }

            for (int controllerIndex = 0; controllerIndex < _options.Controllers.Count; controllerIndex++)
            {
                if (_options.Controllers[controllerIndex].CanRealize(plan, out _))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ReportWarnings(IReadOnlyList<string> warnings)
    {
        for (int index = 0; index < warnings.Count; index++)
        {
            string warning = warnings[index];
            lock (_reportedWarnings)
            {
                if (!_reportedWarnings.Add(warning))
                {
                    continue;
                }
            }

            _options.WarningHandler(warning);
        }
    }

    private static string RequireId(DockerContainerInspectResponse container, string name) =>
        string.IsNullOrWhiteSpace(container.Id)
            ? throw new InvalidDataException($"Docker container '{name}' has no immutable ID.")
            : container.Id;

    private static Dictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> values) =>
        new(values, StringComparer.Ordinal);
}
