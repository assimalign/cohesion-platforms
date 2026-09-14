using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerPlanCompiler
{
    private const string ContentRoot = "/app";
    private const string BootstrapPath = "/var/run/cohesion/bootstrap.token";
    private const string TrustBundlePath = "/var/run/cohesion/trust.pem";
    private const string TelemetryHeadersPath = "/var/run/cohesion/telemetry.headers";

    public void Validate(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
    }

    public ResourcePlan Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ResourcePlan? plan = JsonSerializer.Deserialize(
            json,
            ResourcePlanJsonContext.Default.ResourcePlan);
        return plan ?? throw new JsonException("A Docker resource plan must not be JSON null.");
    }

    public DockerPlanCompilation Compile(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        ApplicationName application,
        string owner,
        string publicHost = "localhost",
        ReadOnlyMemory<byte> telemetryHeaders = default,
        bool renderOnly = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicHost);
        _ = Uri.CreateEndpoint("http", publicHost, 80);

        ValidatePlan(plan);
        IContainerImageArtifact validatedArtifact = ContainerImageArtifacts.Create(
            artifact.Resource,
            $"{artifact.Repository}@{artifact.Digest}",
            artifact.Tag);
        string imageReference = $"{validatedArtifact.Repository}@{validatedArtifact.Digest}";
        string runtimeImage = artifact is DockerImageArtifact dockerArtifact
            ? dockerArtifact.ImageId
            : imageReference;
        string networkName = DockerMetadata.ApplicationNetwork(application);
        string containerName = DockerMetadata.ContainerName(application, plan.Resource);
        string planHash = ComputePlanHash(plan);
        var warnings = new List<string>();
        var hintKeys = new List<string>(plan.Hints.Keys);
        hintKeys.Sort(StringComparer.Ordinal);
        for (int index = 0; index < hintKeys.Count; index++)
        {
            warnings.Add(
                $"Docker compiler ignored unknown advisory hint '{hintKeys[index]}'.");
        }

        Dictionary<string, string> environment = CreateEnvironment(
            plan,
            inputs,
            dependencies,
            publicHost);
        var files = new List<DockerFilePlan>();
        var tmpfs = new List<string>();
        var filePaths = new HashSet<string>(StringComparer.Ordinal);
        var tmpfsPaths = new HashSet<string>(StringComparer.Ordinal);
        var volumeMounts = new List<DockerVolumeMountPlan>();
        var volumes = new List<DockerVolumePlan>();

        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            if (mount.Kind is ResourceMountKind.Volume)
            {
                string volumeName = DockerMetadata.VolumeName(application, plan.Resource, mount.Mount);
                volumeMounts.Add(new DockerVolumeMountPlan(volumeName, mount.ContainerPath));
                volumes.Add(new DockerVolumePlan(
                    volumeName,
                    mount.Mount,
                    mount.ContainerPath,
                    CreateLabels(application, owner, plan.Resource, planHash, runtimeHash: null)));
                continue;
            }

            ReadOnlyMemory<byte> content = renderOnly
                ? ReadOnlyMemory<byte>.Empty
                : RequireInput(inputs, mount).Content;
            AddFile(files, filePaths, mount.ContainerPath, content, mount.Kind is ResourceMountKind.Secret);
            if (mount.Kind is ResourceMountKind.Secret)
            {
                AddTmpfs(tmpfs, tmpfsPaths, GetParentPath(mount.ContainerPath));
            }
        }

        if (!inputs.BootstrapCredential.IsEmpty)
        {
            AddFile(files, filePaths, BootstrapPath, inputs.BootstrapCredential, sensitive: true);
            AddTmpfs(tmpfs, tmpfsPaths, GetParentPath(BootstrapPath));
            environment[ResourceEnvironment.BootstrapTokenPath] = BootstrapPath;
        }

        if (!inputs.TrustBundle.IsEmpty)
        {
            AddFile(files, filePaths, TrustBundlePath, inputs.TrustBundle, sensitive: true);
            AddTmpfs(tmpfs, tmpfsPaths, GetParentPath(TrustBundlePath));
            environment[ResourceEnvironment.TrustBundlePath] = TrustBundlePath;
        }

        if (!telemetryHeaders.IsEmpty)
        {
            AddFile(files, filePaths, TelemetryHeadersPath, telemetryHeaders, sensitive: true);
            AddTmpfs(tmpfs, tmpfsPaths, GetParentPath(TelemetryHeadersPath));
            environment[ResourceEnvironment.TelemetryHeadersPath] = TelemetryHeadersPath;
        }

        var portBindings = CreatePortBindings(plan);
        var aliases = CreateAliases(plan);
        string runtimeHash = ComputeRuntimeHash(
            runtimeImage,
            environment,
            files,
            volumeMounts,
            portBindings,
            tmpfs);
        IReadOnlyDictionary<string, string> labels = CreateLabels(
            application,
            owner,
            plan.Resource,
            planHash,
            runtimeHash);
        var network = new DockerNetworkPlan(
            networkName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerMetadata.ApplicationLabel] = application.ToString(),
                [DockerMetadata.OwnerLabel] = owner,
            });
        var container = new DockerContainerPlan(
            containerName,
            runtimeImage,
            imageReference,
            plan.Workload.Kind,
            plan.Workload.StopGraceSeconds,
            environment,
            labels,
            aliases,
            volumeMounts,
            portBindings,
            tmpfs,
            plan.Workload.RestartPolicy);
        var probes = new List<DockerProbePlan>(plan.Container.Probes.Count);
        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            PortBinding? probePort = probe.Endpoint is null
                ? null
                : FindPort(plan.Container.Ports, probe.Endpoint);
            probes.Add(new DockerProbePlan(
                probe.Role,
                probe.Endpoint,
                probe.Kind,
                probe.Endpoint is null ? null : FindScheme(plan, probe.Endpoint),
                probePort?.ContainerPort,
                probePort?.Protocol,
                probe.Value,
                probe.Command));
        }

        if (NeedsControlPlaneReadiness(plan))
        {
            PortBinding port = FindPort(plan.Container.Ports, plan.ControlPlane.Endpoint);
            probes.Add(new DockerProbePlan(
                "readiness", port.Endpoint, ProbeKind.Http, FindScheme(plan, port.Endpoint),
                port.ContainerPort, port.Protocol, plan.ControlPlane.Path, []));
        }

        return new DockerPlanCompilation(
            application,
            owner,
            planHash,
            runtimeHash,
            network,
            volumes,
            container,
            files,
            probes,
            warnings);
    }

    public static string ComputePlanHash(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        JsonElement document = JsonSerializer.SerializeToElement(
            plan,
            ResourcePlanJsonContext.Default.ResourcePlan);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, document);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    properties.Add(property);
                }

                properties.Sort(static (left, right) =>
                    string.Compare(left.Name, right.Name, StringComparison.Ordinal));
                for (int index = 0; index < properties.Count; index++)
                {
                    writer.WritePropertyName(properties[index].Name);
                    WriteCanonicalJson(writer, properties[index].Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException(
                    $"Resource plan JSON contains unsupported value kind '{value.ValueKind}'.");
        }
    }

    private static void ValidatePlan(ResourcePlan plan)
    {
        if (!string.Equals(plan.Schema, ResourcePlan.CurrentSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Docker compiler does not support plan schema '{plan.Schema}'. Expected '{ResourcePlan.CurrentSchema}'.");
        }

        if (plan.Container.Artifact != ArtifactRef.Self)
        {
            throw new InvalidDataException(
                $"Docker compiler supports only artifact reference '{ArtifactRef.Self}'.");
        }

        if (!Enum.IsDefined(plan.Workload.Kind))
        {
            throw new InvalidDataException($"Unknown workload kind '{plan.Workload.Kind}'.");
        }

        if (plan.Workload.Replicas != 1)
        {
            throw new InvalidDataException(
                $"Docker compiler realizes exactly one container per resource and cannot realize replica count '{plan.Workload.Replicas}'.");
        }

        if (plan.Workload.RestartPolicy is null
            || (plan.Workload.RestartPolicy.Length > 0
                && plan.Workload.RestartPolicy is not "Never" and not "Always" and not "OnFailure"))
        {
            throw new InvalidDataException(
                $"Docker resource '{plan.Resource}' declares unsupported restart policy '{plan.Workload.RestartPolicy}'.");
        }

        if (plan.Workload.StopGraceSeconds < 1)
        {
            throw new InvalidDataException("A Docker workload stop grace must be positive.");
        }

        if (plan.Workload.StableIdentity != (plan.Workload.Kind is WorkloadKind.StatefulSet))
        {
            throw new InvalidDataException(
                "A Docker workload must request stable identity exactly when it is a StatefulSet.");
        }

        ReadinessGate expectedGate = ReadinessGate.For(plan.Workload.Kind);
        if (!SetEquals(plan.Workload.Gate.Terminals, expectedGate.Terminals)
            || !SetEquals(plan.Workload.Gate.Satisfying, expectedGate.Satisfying))
        {
            throw new InvalidDataException(
                $"Docker workload '{plan.Workload.Kind}' has an invalid readiness gate.");
        }

        if (!string.Equals(plan.Container.Name, plan.Resource.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Container name '{plan.Container.Name}' must equal resource '{plan.Resource}'.");
        }

        _ = DockerMetadata.Normalize(plan.Container.Name);
        foreach ((string name, string? value) in plan.Container.Environment)
        {
            RequireEnvironmentName(name);
            if (value is null)
            {
                throw new InvalidDataException($"Docker environment variable '{name}' has a null value.");
            }
        }

        var services = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (!services.Add(service.Name))
            {
                throw new InvalidDataException($"Service '{service.Name}' is declared more than once.");
            }

            _ = DockerMetadata.Normalize(service.Name);
            RequireProtocol(service.Protocol, $"service '{service.Name}'");
            if (service.Endpoint is null)
            {
                if (!service.Headless || !service.Governing || service.Port is not null)
                {
                    throw new InvalidDataException(
                        $"Portless service '{service.Name}' must be headless and governing.");
                }
            }
            else if (service.Headless || service.Governing || service.Port is not (>= 1 and <= 65535))
            {
                throw new InvalidDataException(
                    $"Endpoint service '{service.Name}' must be non-headless, non-governing, and declare a valid port.");
            }
        }

        var ports = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            if (!ports.Add(port.Endpoint) || port.ContainerPort is < 1 or > 65535)
            {
                throw new InvalidDataException($"Endpoint port '{port.Endpoint}' is duplicated or invalid.");
            }

            RequireProtocol(port.Protocol, $"endpoint '{port.Endpoint}'");
            if (port.Certificate is null)
            {
                throw new InvalidDataException(
                    $"Docker endpoint '{port.Endpoint}' certificate mount must not be null.");
            }

            if (!string.IsNullOrEmpty(port.Certificate)
                && !string.Equals(port.Certificate, "public", StringComparison.OrdinalIgnoreCase))
            {
                MountBinding? certificateMount = FindMount(plan.Container.Mounts, port.Certificate);
                if (certificateMount?.Kind is not ResourceMountKind.Secret)
                {
                    throw new InvalidDataException(
                        $"Docker endpoint '{port.Endpoint}' certificate mount '{port.Certificate}' must name a Secret mount.");
                }
            }
            ServiceSpec service = FindService(plan.Services, port.Endpoint);
            if (service.Port != port.ContainerPort
                || !string.Equals(service.Protocol, port.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Service '{service.Name}' does not match endpoint '{port.Endpoint}'.");
            }
        }

        var mountNames = new HashSet<string>(StringComparer.Ordinal);
        var mountPaths = new HashSet<string>(StringComparer.Ordinal);
        int volumeMounts = 0;
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            if (!Enum.IsDefined(mount.Kind))
            {
                throw new InvalidDataException($"Mount '{mount.Mount}' has unknown kind '{mount.Kind}'.");
            }

            if (!mountNames.Add(mount.Mount))
            {
                throw new InvalidDataException($"Mount '{mount.Mount}' is declared more than once.");
            }

            if (!mountPaths.Add(mount.ContainerPath))
            {
                throw new InvalidDataException(
                    $"Container path '{mount.ContainerPath}' is used by more than one mount.");
            }

            RequireAbsoluteContainerPath(mount.ContainerPath, $"mount '{mount.Mount}'");
            if (mount.Kind is ResourceMountKind.Volume)
            {
                volumeMounts++;
                if (!ContainsVolume(plan.Volumes, mount.Mount))
                {
                    throw new InvalidDataException(
                        $"Volume mount '{mount.Mount}' has no matching volume specification.");
                }
            }
        }

        var volumeNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            if (!volumeNames.Add(volume.Name))
            {
                throw new InvalidDataException($"Volume '{volume.Name}' is declared more than once.");
            }

            if (volume.Kind is not ResourceMountKind.Volume
                || !volume.PerReplicaClaim
                || string.IsNullOrWhiteSpace(volume.Size))
            {
                throw new InvalidDataException(
                    $"Volume '{volume.Name}' must be a sized per-replica Volume claim.");
            }

            MountBinding? mount = FindMount(plan.Container.Mounts, volume.Name);
            if (mount is null || mount.Kind is not ResourceMountKind.Volume)
            {
                throw new InvalidDataException(
                    $"Volume '{volume.Name}' has no matching Volume mount.");
            }
        }

        if (volumeMounts != plan.Volumes.Count)
        {
            throw new InvalidDataException(
                "Every Docker Volume mount must have exactly one named-volume claim.");
        }

        var probeRoles = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            if (probe.Role is not "readiness" and not "liveness" and not "startup"
                || !probeRoles.Add(probe.Role))
            {
                throw new InvalidDataException(
                    $"Probe role '{probe.Role}' is unknown or declared more than once.");
            }

            if (!Enum.IsDefined(probe.Kind))
            {
                throw new InvalidDataException($"Unknown probe kind '{probe.Kind}'.");
            }

            if (probe.Kind is ProbeKind.Grpc)
            {
                throw new InvalidDataException(
                    "Docker compiler does not support gRPC health probes; use HTTP, TCP, exec, or none.");
            }

            if (probe.Kind is ProbeKind.Http or ProbeKind.Tcp)
            {
                if (string.IsNullOrWhiteSpace(probe.Endpoint))
                {
                    throw new InvalidDataException(
                        $"Docker {probe.Role} {probe.Kind} probe must name an endpoint.");
                }

                PortBinding port = FindPort(plan.Container.Ports, probe.Endpoint);
                if (!string.Equals(port.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Docker {probe.Role} {probe.Kind} probe cannot target UDP endpoint '{probe.Endpoint}'.");
                }
            }

            if (probe.Kind is ProbeKind.Http
                && (string.IsNullOrWhiteSpace(probe.Value)
                    || !probe.Value.StartsWith("/", StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Docker {probe.Role} HTTP probe must declare an absolute path.");
            }

            if (probe.Kind is ProbeKind.Exec
                && (probe.Command.Count == 0 || string.IsNullOrWhiteSpace(probe.Command[0])))
            {
                throw new InvalidDataException(
                    $"Docker {probe.Role} exec probe must declare a non-empty command.");
            }
        }

        if (NeedsControlPlaneReadiness(plan))
        {
            PortBinding port = FindPort(plan.Container.Ports, plan.ControlPlane.Endpoint);
            if (!string.Equals(port.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
                || !plan.ControlPlane.Path.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Docker control-plane readiness endpoint '{port.Endpoint}' requires TCP and an absolute path.");
            }
        }

        var exposureNames = new HashSet<string>(StringComparer.Ordinal);
        var exposureEndpoints = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            ExposureSpec exposure = plan.Exposures[index];
            if (!exposureNames.Add(exposure.Name)
                || !exposureEndpoints.Add(exposure.Endpoint)
                || exposure.Port is < 1 or > 65535)
            {
                throw new InvalidDataException(
                    $"Exposure '{exposure.Name}' is duplicated or has an invalid port.");
            }

            RequireProtocol(exposure.Protocol, $"exposure '{exposure.Name}'");
            if (string.IsNullOrWhiteSpace(exposure.Scheme))
            {
                throw new InvalidDataException(
                    $"Docker exposure '{exposure.Name}' must declare a URI scheme.");
            }

            _ = Uri.CreateEndpoint(exposure.Scheme, "localhost", exposure.Port);
            PortBinding port = FindPort(plan.Container.Ports, exposure.Endpoint);
            ServiceSpec service = FindService(plan.Services, exposure.Endpoint);
            if (!string.Equals(service.Name, exposure.Service, StringComparison.Ordinal)
                || service.Port != exposure.Port
                || port.ContainerPort != exposure.Port
                || !string.Equals(port.Protocol, exposure.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Exposure '{exposure.Name}' does not match endpoint '{exposure.Endpoint}' and its service.");
            }
        }
    }

    private static Dictionary<string, string> CreateEnvironment(
        ResourcePlan plan,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        string publicHost)
    {
        var environment = new Dictionary<string, string>(
            plan.Container.Environment,
            StringComparer.Ordinal)
        {
            [ResourceEnvironment.Gateway] = "docker",
            [ResourceEnvironment.ContentRoot] = ContentRoot,
        };

        if (!inputs.ApplicationTrustKey.IsEmpty)
        {
            environment[ResourceEnvironment.ApplicationTrustKey] =
                Encoding.UTF8.GetString(inputs.ApplicationTrustKey.Span);
        }

        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            environment[ResourceEnvironment.Mount(mount.Mount)] = mount.ContainerPath;
        }

        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            environment[ResourceEnvironment.Endpoint(port.Endpoint, "HOST")] = "0.0.0.0";
            environment[ResourceEnvironment.Endpoint(port.Endpoint, "PORT")] =
                port.ContainerPort.ToString(CultureInfo.InvariantCulture);
            environment[ResourceEnvironment.Endpoint(port.Endpoint, "SCHEME")] =
                FindScheme(plan, port.Endpoint);
            ExposureSpec? exposure = FindExposure(plan.Exposures, port.Endpoint);
            if (exposure is null)
            {
                environment.Remove(ResourceEnvironment.Endpoint(port.Endpoint, "PUBLIC_URL"));
            }
            else
            {
                environment[ResourceEnvironment.Endpoint(port.Endpoint, "PUBLIC_URL")] =
                    Uri.CreateEndpoint(exposure.Scheme, publicHost, exposure.Port)
                        .ToEndpointString();
            }
        }

        ApplyDependencyEnvironment(dependencies, environment);
        return environment;
    }

    private static IReadOnlyList<DockerPortPublishPlan> CreatePortBindings(ResourcePlan plan)
    {
        var result = new List<DockerPortPublishPlan>();
        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            ExposureSpec exposure = plan.Exposures[index];
            result.Add(new DockerPortPublishPlan(
                exposure.Endpoint,
                exposure.Port,
                exposure.Protocol.ToLowerInvariant(),
                "0.0.0.0",
                exposure.Port,
                ProbeOnly: false));
        }

        var probeEndpoints = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            if (probe.Kind is not ProbeKind.Http and not ProbeKind.Tcp
                || probe.Endpoint is null
                || !probeEndpoints.Add(probe.Endpoint))
            {
                continue;
            }

            PortBinding port = FindPort(plan.Container.Ports, probe.Endpoint);
            result.Add(new DockerPortPublishPlan(
                port.Endpoint,
                port.ContainerPort,
                port.Protocol.ToLowerInvariant(),
                "127.0.0.1",
                HostPort: null,
                ProbeOnly: true));
        }

        if (NeedsControlPlaneReadiness(plan) && probeEndpoints.Add(plan.ControlPlane.Endpoint))
        {
            PortBinding port = FindPort(plan.Container.Ports, plan.ControlPlane.Endpoint);
            result.Add(new DockerPortPublishPlan(
                port.Endpoint, port.ContainerPort, port.Protocol.ToLowerInvariant(),
                "127.0.0.1", HostPort: null, ProbeOnly: true));
        }

        return result;
    }

    private static IReadOnlyList<string> CreateAliases(ResourcePlan plan)
    {
        var aliases = new List<string> { DockerMetadata.Normalize(plan.Resource.ToString()) };
        var seen = new HashSet<string>(aliases, StringComparer.Ordinal);
        for (int index = 0; index < plan.Services.Count; index++)
        {
            string alias = DockerMetadata.Normalize(plan.Services[index].Name);
            if (seen.Add(alias))
            {
                aliases.Add(alias);
            }
        }

        return aliases;
    }

    private static IReadOnlyDictionary<string, string> CreateLabels(
        ApplicationName application,
        string owner,
        ResourceName resource,
        string planHash,
        string? runtimeHash)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerMetadata.ApplicationLabel] = application.ToString(),
            [DockerMetadata.OwnerLabel] = owner,
            [DockerMetadata.ResourceLabel] = resource.ToString(),
            [DockerMetadata.PlanHashLabel] = planHash,
        };
        if (runtimeHash is not null)
        {
            labels[DockerMetadata.RuntimeHashLabel] = runtimeHash;
        }

        return labels;
    }

    private static ResourceMountInput RequireInput(ResourceInputs inputs, MountBinding mount)
    {
        if (!inputs.Mounts.TryGetValue(mount.Mount, out ResourceMountInput? input))
        {
            throw new InvalidOperationException(
                $"Resolved inputs do not contain planned {mount.Kind} mount '{mount.Mount}'.");
        }

        if (!input.IsResolved)
        {
            throw new InvalidOperationException(
                $"Mount '{mount.Mount}' is unresolved: {input.UnresolvedReason}");
        }

        return input;
    }

    private static void AddFile(
        ICollection<DockerFilePlan> files,
        ISet<string> paths,
        string path,
        ReadOnlyMemory<byte> content,
        bool sensitive)
    {
        if (!paths.Add(path))
        {
            throw new InvalidDataException(
                $"Docker runtime input path '{path}' is populated more than once.");
        }

        files.Add(new DockerFilePlan(path, content.ToArray(), sensitive));
    }

    private static void AddTmpfs(
        ICollection<string> tmpfs,
        ISet<string> paths,
        string path)
    {
        if (paths.Add(path))
        {
            tmpfs.Add(path);
        }
    }

    private static string GetParentPath(string path)
    {
        int separator = path.LastIndexOf('/');
        if (separator <= 0 || separator == path.Length - 1)
        {
            throw new InvalidDataException(
                $"Docker file input path '{path}' must name a file beneath an absolute directory.");
        }

        return path[..separator];
    }

    private static void ApplyDependencyEnvironment(
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        IDictionary<string, string> environment)
    {
        var projected = new Dictionary<string, DependencyProjection>(StringComparer.Ordinal);
        for (int dependencyIndex = 0; dependencyIndex < dependencies.Count; dependencyIndex++)
        {
            ResourceDependencyObservation dependency = dependencies[dependencyIndex];
            for (int endpointIndex = 0; endpointIndex < dependency.RequestedEndpoints.Count; endpointIndex++)
            {
                string endpointName = dependency.RequestedEndpoints[endpointIndex];
                ResourceEndpoint? endpoint = dependency.Optional
                    && dependency.State is not ResourceLifecycle.Running
                    && dependency.State is not ResourceLifecycle.Degraded
                        ? null
                        : FindObservedDependencyEndpoint(dependency, endpointName);
                string variable = ResourceEnvironment.Dependency(
                    dependency.Resource.ToString(),
                    endpointName,
                    "URL");
                var projection = new DependencyProjection(
                    dependency.Application,
                    dependency.Resource,
                    endpointName,
                    endpoint);
                if (projected.TryGetValue(variable, out DependencyProjection previous))
                {
                    if (previous.Application == projection.Application
                        && previous.Resource == projection.Resource
                        && string.Equals(previous.EndpointName, endpointName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new InvalidDataException(
                        $"Dependency endpoint '{projection.Application}/{projection.Resource}/{endpointName}' " +
                        $"collides with '{previous.Application}/{previous.Resource}/{previous.EndpointName}' " +
                        "after environment-name normalization.");
                }

                projected.Add(variable, projection);
            }
        }

        foreach (DependencyProjection projection in projected.Values)
        {
            string resource = projection.Resource.ToString();
            RemoveDependencyEnvironment(environment, resource, projection.EndpointName);
            if (projection.Endpoint is ResourceEndpoint endpoint)
            {
                ApplyDependencyEndpoint(environment, resource, endpoint);
            }
        }
    }

    private static ResourceEndpoint FindObservedDependencyEndpoint(
        ResourceDependencyObservation dependency,
        string endpointName)
    {
        ResourceEndpoint? publicEndpoint = null;
        for (int index = 0; index < dependency.Endpoints.Count; index++)
        {
            ResourceEndpoint endpoint = dependency.Endpoints[index];
            if (!string.Equals(endpoint.Name, endpointName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!endpoint.IsPublic)
            {
                return endpoint;
            }

            publicEndpoint ??= endpoint;
        }

        if (publicEndpoint is ResourceEndpoint observed)
        {
            return observed;
        }

        throw new InvalidOperationException(
            $"Dependency '{dependency.Application}/{dependency.Resource}' reached " +
            $"'{dependency.State}' without requested endpoint '{endpointName}' in observed state.");
    }

    private static void RemoveDependencyEnvironment(
        IDictionary<string, string> environment,
        string resource,
        string endpoint)
    {
        environment.Remove(ResourceEnvironment.Dependency(resource, endpoint, "URL"));
        environment.Remove(ResourceEnvironment.Dependency(resource, endpoint, "HOST"));
        environment.Remove(ResourceEnvironment.Dependency(resource, endpoint, "PORT"));
        environment.Remove(ResourceEnvironment.Dependency(resource, endpoint, "SCHEME"));
    }

    private static void ApplyDependencyEndpoint(
        IDictionary<string, string> environment,
        string resource,
        ResourceEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host)
            || string.IsNullOrWhiteSpace(endpoint.Scheme)
            || endpoint.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Dependency '{resource}' reached Running without a complete observed address " +
                $"for endpoint '{endpoint.Name}'.");
        }

        Uri address = Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host, endpoint.Port);
        environment[ResourceEnvironment.Dependency(resource, endpoint.Name, "URL")] =
            address.ToEndpointString();
        environment[ResourceEnvironment.Dependency(resource, endpoint.Name, "HOST")] = address.IdnHost;
        environment[ResourceEnvironment.Dependency(resource, endpoint.Name, "PORT")] =
            address.Port.ToString(CultureInfo.InvariantCulture);
        environment[ResourceEnvironment.Dependency(resource, endpoint.Name, "SCHEME")] = address.Scheme;
    }

    private static string ComputeRuntimeHash(
        string image,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<DockerFilePlan> files,
        IReadOnlyList<DockerVolumeMountPlan> mounts,
        IReadOnlyList<DockerPortPublishPlan> ports,
        IReadOnlyList<string> tmpfs)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, image);

        var environmentKeys = new List<string>(environment.Keys);
        environmentKeys.Sort(StringComparer.Ordinal);
        for (int index = 0; index < environmentKeys.Count; index++)
        {
            string key = environmentKeys[index];
            Append(hash, key);
            Append(hash, environment[key]);
        }

        for (int index = 0; index < files.Count; index++)
        {
            Append(hash, files[index].Path);
            hash.AppendData(files[index].Content.Span);
            hash.AppendData([0]);
        }

        for (int index = 0; index < mounts.Count; index++)
        {
            Append(hash, mounts[index].Source);
            Append(hash, mounts[index].Target);
        }

        for (int index = 0; index < ports.Count; index++)
        {
            DockerPortPublishPlan port = ports[index];
            Append(hash, port.Endpoint);
            Append(hash, port.ContainerPort.ToString(CultureInfo.InvariantCulture));
            Append(hash, port.Protocol);
            Append(hash, port.HostIp);
            Append(hash, port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

        for (int index = 0; index < tmpfs.Count; index++)
        {
            Append(hash, tmpfs[index]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string FindScheme(ResourcePlan plan, string endpoint)
    {
        PortBinding port = FindPort(plan.Container.Ports, endpoint);
        if (!string.IsNullOrEmpty(port.Scheme))
        {
            return port.Scheme;
        }

        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            ExposureSpec exposure = plan.Exposures[index];
            if (string.Equals(exposure.Endpoint, endpoint, StringComparison.Ordinal))
            {
                return exposure.Scheme;
            }
        }

        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            if (probe.Kind is ProbeKind.Http
                && string.Equals(probe.Endpoint, endpoint, StringComparison.Ordinal))
            {
                return "http";
            }
        }

        return string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase)
            ? "udp"
            : "tcp";
    }

    private static bool NeedsControlPlaneReadiness(ResourcePlan plan)
    {
        if (string.IsNullOrEmpty(plan.ControlPlane.Endpoint))
        {
            return false;
        }

        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            if (plan.Container.Probes[index].Role == "readiness")
            {
                return false;
            }
        }

        return true;
    }

    private static ServiceSpec FindService(IReadOnlyList<ServiceSpec> services, string endpoint)
    {
        for (int index = 0; index < services.Count; index++)
        {
            if (string.Equals(services[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return services[index];
            }
        }

        throw new InvalidDataException(
            $"Endpoint '{endpoint}' has no Docker network service in the resource plan.");
    }

    private static ExposureSpec? FindExposure(
        IReadOnlyList<ExposureSpec> exposures,
        string endpoint)
    {
        for (int index = 0; index < exposures.Count; index++)
        {
            if (string.Equals(exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return exposures[index];
            }
        }

        return null;
    }

    private static PortBinding FindPort(IReadOnlyList<PortBinding> ports, string endpoint)
    {
        for (int index = 0; index < ports.Count; index++)
        {
            if (string.Equals(ports[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return ports[index];
            }
        }

        throw new InvalidDataException($"Endpoint '{endpoint}' has no container port binding.");
    }

    private static MountBinding? FindMount(IReadOnlyList<MountBinding> mounts, string name)
    {
        for (int index = 0; index < mounts.Count; index++)
        {
            if (string.Equals(mounts[index].Mount, name, StringComparison.Ordinal))
            {
                return mounts[index];
            }
        }

        return null;
    }

    private static bool ContainsVolume(IReadOnlyList<VolumeSpec> volumes, string name)
    {
        for (int index = 0; index < volumes.Count; index++)
        {
            if (string.Equals(volumes[index].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireProtocol(string protocol, string description)
    {
        if (!string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Docker {description} has unsupported protocol '{protocol}'; expected tcp or udp.");
        }
    }

    private static void RequireAbsoluteContainerPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("/../", StringComparison.Ordinal)
            || path.EndsWith("/..", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Docker {description} must use a normalized absolute Linux container path.");
        }
    }

    private static void RequireEnvironmentName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || !IsEnvironmentInitial(name[0]))
        {
            throw new InvalidDataException(
                $"Docker environment variable '{name}' must match [A-Za-z_][A-Za-z0-9_]*.");
        }

        for (int index = 1; index < name.Length; index++)
        {
            char character = name[index];
            if (!IsEnvironmentInitial(character) && character is not (>= '0' and <= '9'))
            {
                throw new InvalidDataException(
                    $"Docker environment variable '{name}' must match [A-Za-z_][A-Za-z0-9_]*.");
            }
        }
    }

    private static bool IsEnvironmentInitial(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool SetEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : notnull
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var values = new HashSet<T>(left);
        return values.SetEquals(right);
    }

    private readonly record struct DependencyProjection(
        ApplicationName Application,
        ResourceName Resource,
        string EndpointName,
        ResourceEndpoint? Endpoint);
}
