using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using k8s;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesPlanCompiler
{
    private const string ContentRoot = "/app";
    private const string BootstrapDirectory = "/var/run/cohesion";
    private const string BootstrapPath = "/var/run/cohesion/bootstrap.token";
    private const string TrustBundlePath = "/var/run/cohesion/trust.pem";
    private const string TelemetryHeadersPath = "/var/run/cohesion/telemetry.headers";
    private const string BootstrapVolumeName = "cohesion-bootstrap";
    private const string ConfigurationVolumeName = "cohesion-configuration";
    private const string SecretVolumeName = "cohesion-secret";

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
        return plan ?? throw new JsonException("A Kubernetes resource plan must not be JSON null.");
    }

    public KubernetesPlanCompilation Compile(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        string namespaceName,
        string owner,
        KubernetesGatewayOptions options,
        IReadOnlyList<ResourceEndpoint>? ownEndpoints = null,
        ReadOnlyMemory<byte> telemetryHeaders = default,
        bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(options);

        ValidatePlan(plan);
        IContainerImageArtifact validatedArtifact = ContainerImageArtifacts.Create(
            artifact.Resource,
            $"{artifact.Repository}@{artifact.Digest}",
            artifact.Tag);
        KubernetesMetadata.RequireDnsLabel(namespaceName, "namespace");
        string resource = KubernetesMetadata.ResourceName(plan.Resource);
        string planHash = ComputePlanHash(plan);
        string configMapName = KubernetesMetadata.ChildName(resource, "configuration");
        string secretName = KubernetesMetadata.ChildName(resource, "secret");
        var objects = new List<IKubernetesObject<V1ObjectMeta>>();
        var warnings = new List<string>();

        foreach ((string hint, _) in plan.Hints)
        {
            warnings.Add($"Kubernetes compiler ignored unknown advisory hint '{hint}'.");
        }

        string restartPolicy = ResolveRestartPolicy(plan, warnings);

        Dictionary<string, string> environment = CreateEnvironment(
            plan,
            inputs,
            dependencies,
            ownEndpoints ?? Array.Empty<ResourceEndpoint>());
        Dictionary<string, byte[]> configurationData = CreateMountData(
            plan,
            inputs,
            ResourceMountKind.Configuration,
            preview);
        Dictionary<string, byte[]> secretData = CreateMountData(
            plan,
            inputs,
            ResourceMountKind.Secret,
            preview);
        var bootstrapItems = new List<V1KeyToPath>();

        if (!inputs.BootstrapCredential.IsEmpty)
        {
            secretData["bootstrap-token"] = inputs.BootstrapCredential.ToArray();
            environment[ResourceEnvironment.BootstrapTokenPath] = BootstrapPath;
            bootstrapItems.Add(new V1KeyToPath { Key = "bootstrap-token", Path = "bootstrap.token" });
        }

        if (!inputs.TrustBundle.IsEmpty)
        {
            secretData["trust-bundle"] = inputs.TrustBundle.ToArray();
            environment[ResourceEnvironment.TrustBundlePath] = TrustBundlePath;
            bootstrapItems.Add(new V1KeyToPath { Key = "trust-bundle", Path = "trust.pem" });
        }

        if (!telemetryHeaders.IsEmpty)
        {
            secretData["telemetry-headers"] = telemetryHeaders.ToArray();
            environment[ResourceEnvironment.TelemetryHeadersPath] = TelemetryHeadersPath;
            bootstrapItems.Add(new V1KeyToPath { Key = "telemetry-headers", Path = "telemetry.headers" });
        }

        RequireDisjointKeys(environment, configurationData);

        var configMap = new V1ConfigMap
        {
            ApiVersion = V1ConfigMap.KubeApiVersion,
            Kind = V1ConfigMap.KubeKind,
            Metadata = KubernetesMetadata.CreateObjectMeta(
                configMapName,
                namespaceName,
                resource,
                planHash,
                owner),
            Data = environment,
            BinaryData = configurationData,
        };
        AddPatched(objects, plan.Resource, configMap, configMapName, namespaceName, resource, planHash, owner, options);

        var secret = new V1Secret
        {
            ApiVersion = V1Secret.KubeApiVersion,
            Kind = V1Secret.KubeKind,
            Metadata = KubernetesMetadata.CreateObjectMeta(
                secretName,
                namespaceName,
                resource,
                planHash,
                owner),
            Type = "Opaque",
            Data = secretData,
        };
        AddPatched(objects, plan.Resource, secret, secretName, namespaceName, resource, planHash, owner, options);

        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            V1Service compiled = CreateService(
                plan,
                service,
                namespaceName,
                resource,
                planHash,
                owner);
            AddPatched(
                objects,
                plan.Resource,
                compiled,
                compiled.Metadata.Name,
                namespaceName,
                resource,
                planHash,
                owner,
                options);
        }

        var claims = new List<V1PersistentVolumeClaim>();
        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            V1PersistentVolumeClaim claim = CreateClaim(
                volume,
                namespaceName,
                resource,
                planHash,
                owner,
                includeNamespace: plan.Workload.Kind is not WorkloadKind.StatefulSet);
            if (plan.Workload.Kind is WorkloadKind.StatefulSet)
            {
                options.ApplyPatches(plan.Resource, claim);
                KubernetesMetadata.RestoreRequiredMetadata(
                    claim.Metadata,
                    volume.Name,
                    namespaceName,
                    resource,
                    planHash,
                    owner);
                claim.Metadata.NamespaceProperty = null;
            }

            claims.Add(claim);
        }

        if (plan.Workload.Kind is not WorkloadKind.StatefulSet)
        {
            for (int index = 0; index < claims.Count; index++)
            {
                V1PersistentVolumeClaim claim = claims[index];
                AddPatched(
                    objects,
                    plan.Resource,
                    claim,
                    claim.Metadata.Name,
                    namespaceName,
                    resource,
                    planHash,
                    owner,
                    options);
            }
        }

        IKubernetesObject<V1ObjectMeta> workload = CreateWorkload(
            plan,
            validatedArtifact,
            namespaceName,
            resource,
            planHash,
            owner,
            configMapName,
            secretName,
            claims,
            bootstrapItems,
            restartPolicy);
        AddPatched(
            objects,
            plan.Resource,
            workload,
            resource,
            namespaceName,
            resource,
            planHash,
            owner,
            options);
        RestoreStatefulClaimMetadata(
            workload,
            plan,
            namespaceName,
            resource,
            planHash,
            owner);
        RestorePodMetadata(workload, resource, planHash, owner);

        return new KubernetesPlanCompilation(
            namespaceName,
            planHash,
            objects,
            new KubernetesReadinessRule(plan.Workload.Kind, plan.Workload.Replicas),
            CreateObservedEndpoints(plan, namespaceName),
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
                $"Kubernetes compiler does not support plan schema '{plan.Schema}'. Expected '{ResourcePlan.CurrentSchema}'.");
        }

        if (plan.Container.Artifact != ArtifactRef.Self)
        {
            throw new InvalidDataException(
                $"Kubernetes compiler supports only artifact reference '{ArtifactRef.Self}'.");
        }

        if (!Enum.IsDefined(plan.Workload.Kind))
        {
            throw new InvalidDataException($"Unknown workload kind '{plan.Workload.Kind}'.");
        }

        if (plan.Workload.Replicas < 1)
        {
            throw new InvalidDataException("A Kubernetes workload must request at least one replica.");
        }

        if (plan.Workload.StopGraceSeconds < 1)
        {
            throw new InvalidDataException("A Kubernetes workload stop grace must be positive.");
        }

        if (plan.Workload.StableIdentity != (plan.Workload.Kind is WorkloadKind.StatefulSet))
        {
            throw new InvalidDataException(
                "A Kubernetes workload must request stable identity exactly when it is a StatefulSet.");
        }

        ReadinessGate expectedGate = ReadinessGate.For(plan.Workload.Kind);
        if (!SetEquals(plan.Workload.Gate.Terminals, expectedGate.Terminals)
            || !SetEquals(plan.Workload.Gate.Satisfying, expectedGate.Satisfying))
        {
            throw new InvalidDataException(
                $"Kubernetes workload '{plan.Workload.Kind}' has an invalid readiness gate.");
        }

        string resourceName = KubernetesMetadata.ResourceName(plan.Resource);
        _ = KubernetesMetadata.ChildName(resourceName, "configuration");
        _ = KubernetesMetadata.ChildName(resourceName, "secret");
        KubernetesMetadata.RequireDnsLabel(plan.Container.Name, "container name");

        foreach ((string name, string value) in plan.Container.Environment)
        {
            RequireEnvironmentName(name);
            if (value is null)
            {
                throw new InvalidDataException(
                    $"Kubernetes environment variable '{name}' has a null value.");
            }
        }

        var serviceNames = new HashSet<string>(StringComparer.Ordinal);
        int governingServices = 0;
        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            KubernetesMetadata.RequireDnsLabel(service.Name, "service name");
            if (!serviceNames.Add(service.Name))
            {
                throw new InvalidDataException($"Service '{service.Name}' is declared more than once.");
            }

            if (service.Port is not null && service.Port is < 1 or > 65535)
            {
                throw new InvalidDataException($"Service '{service.Name}' has invalid port '{service.Port}'.");
            }

            RequireProtocol(service.Protocol, $"service '{service.Name}'");

            if (service.Endpoint is null)
            {
                if (!service.Headless || !service.Governing || service.Port is not null)
                {
                    throw new InvalidDataException(
                        $"Portless service '{service.Name}' must be a headless governing Service.");
                }

                governingServices++;
            }
            else if (service.Headless || service.Governing || service.Port is null)
            {
                throw new InvalidDataException(
                    $"Endpoint service '{service.Name}' must be non-headless, non-governing, and declare a port.");
            }
        }

        int expectedGoverningServices = plan.Workload.Kind is WorkloadKind.StatefulSet ? 1 : 0;
        if (governingServices != expectedGoverningServices)
        {
            throw new InvalidDataException(
                $"Kubernetes workload '{plan.Workload.Kind}' requires {expectedGoverningServices} " +
                $"headless governing Service, but the plan declares {governingServices}.");
        }

        var ports = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            if (!ports.Add(port.Endpoint) || port.ContainerPort is < 1 or > 65535)
            {
                throw new InvalidDataException($"Endpoint port '{port.Endpoint}' is duplicated or invalid.");
            }

            if (port.Endpoint.Length > 15)
            {
                throw new InvalidDataException(
                    $"Endpoint '{port.Endpoint}' exceeds the Kubernetes port-name limit of 15 characters.");
            }

            KubernetesMetadata.RequireDnsLabel(port.Endpoint, "port name");

            RequireProtocol(port.Protocol, $"endpoint '{port.Endpoint}'");

            if (port.Certificate is null)
            {
                throw new InvalidDataException($"Endpoint '{port.Endpoint}' certificate must not be null.");
            }

            if (port.Certificate.Length > 0
                && !string.Equals(port.Certificate, "public", StringComparison.OrdinalIgnoreCase))
            {
                MountBinding? certificateMount = FindMount(plan.Container.Mounts, port.Certificate);
                if (certificateMount is null || certificateMount.Kind is not ResourceMountKind.Secret)
                {
                    throw new InvalidDataException(
                        $"Endpoint '{port.Endpoint}' certificate mount '{port.Certificate}' must name a Secret mount.");
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

        var mounts = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            if (!Enum.IsDefined(mount.Kind))
            {
                throw new InvalidDataException(
                    $"Mount '{mount.Mount}' has unknown kind '{mount.Kind}'.");
            }

            if (!mounts.Add(mount.Mount))
            {
                throw new InvalidDataException($"Mount '{mount.Mount}' is declared more than once.");
            }

            if (mount.Kind is ResourceMountKind.Secret
                && mount.Mount is "bootstrap-token" or "trust-bundle" or "telemetry-headers")
            {
                throw new InvalidDataException(
                    $"Secret mount '{mount.Mount}' conflicts with a reserved gateway bootstrap credential key.");
            }

            if (mount.Kind is ResourceMountKind.Volume
                && mount.Mount is ConfigurationVolumeName or SecretVolumeName or BootstrapVolumeName)
            {
                throw new InvalidDataException(
                    $"Volume mount '{mount.Mount}' conflicts with a Kubernetes gateway-managed volume.");
            }

            RequireDataKey(mount.Mount);
            if (string.IsNullOrWhiteSpace(mount.ContainerPath)
                || !mount.ContainerPath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Mount '{mount.Mount}' must use an absolute Linux container path.");
            }
        }

        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            KubernetesMetadata.RequireDnsLabel(volume.Name, "volume name");
            try
            {
                _ = new ResourceQuantity(volume.Size);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                throw new InvalidDataException(
                    $"Volume '{volume.Name}' has invalid Kubernetes quantity '{volume.Size}'.",
                    exception);
            }

            if (!volume.PerReplicaClaim || volume.Kind is not ResourceMountKind.Volume)
            {
                throw new InvalidDataException(
                    $"Volume '{volume.Name}' must be a per-replica Volume claim.");
            }

            MountBinding? binding = FindMount(plan.Container.Mounts, volume.Name);
            if (binding is null || binding.Kind is not ResourceMountKind.Volume)
            {
                throw new InvalidDataException(
                    $"Volume '{volume.Name}' has no matching Volume mount.");
            }
        }

        int volumeMounts = 0;
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            if (mount.Kind is not ResourceMountKind.Volume)
            {
                continue;
            }

            volumeMounts++;
            if (!ContainsVolume(plan.Volumes, mount.Mount))
            {
                throw new InvalidDataException(
                    $"Volume mount '{mount.Mount}' has no matching volume specification.");
            }
        }

        if (volumeMounts != plan.Volumes.Count
            || (volumeMounts > 0) != (plan.Workload.Kind is WorkloadKind.StatefulSet))
        {
            throw new InvalidDataException(
                "Kubernetes per-replica volumes require a StatefulSet and every StatefulSet requires a volume claim.");
        }

        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            if (probe.Role is not "readiness" and not "liveness" and not "startup")
            {
                throw new InvalidDataException($"Unknown probe role '{probe.Role}'.");
            }

            if (!Enum.IsDefined(probe.Kind))
            {
                throw new InvalidDataException($"Unknown probe kind '{probe.Kind}'.");
            }
        }


        if (NeedsControlPlaneReadiness(plan))
        {
            PortBinding port = FindPort(plan.Container.Ports, plan.ControlPlane.Endpoint);
            string scheme = FindScheme(plan, port.Endpoint);
            if (!string.Equals(port.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
                || (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                || string.IsNullOrEmpty(plan.ControlPlane.Path)
                || !plan.ControlPlane.Path.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Kubernetes control-plane readiness endpoint '{port.Endpoint}' requires HTTP or HTTPS over TCP and an absolute path.");
            }
        }

        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            ExposureSpec exposure = plan.Exposures[index];
            if (exposure.Port is < 1 or > 65535)
            {
                throw new InvalidDataException(
                    $"Exposure '{exposure.Name}' has invalid port '{exposure.Port}'.");
            }

            RequireProtocol(exposure.Protocol, $"exposure '{exposure.Name}'");
            ServiceSpec service = FindService(plan.Services, exposure.Endpoint);
            if (!string.Equals(service.Name, exposure.Service, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Exposure '{exposure.Name}' does not target endpoint Service '{service.Name}'.");
            }
        }
    }

    private static Dictionary<string, string> CreateEnvironment(
        ResourcePlan plan,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        IReadOnlyList<ResourceEndpoint> ownEndpoints)
    {
        var environment = new Dictionary<string, string>(plan.Container.Environment, StringComparer.Ordinal)
        {
            [ResourceEnvironment.Gateway] = "kubernetes",
            [ResourceEnvironment.ContentRoot] = ContentRoot,
        };
        environment.Remove(ResourceEnvironment.BootstrapTokenPath);
        environment.Remove(ResourceEnvironment.TrustBundlePath);
        environment.Remove(ResourceEnvironment.TelemetryHeadersPath);

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
            PortBinding binding = plan.Container.Ports[index];
            ServiceSpec service = FindService(plan.Services, binding.Endpoint);
            environment[ResourceEnvironment.Endpoint(binding.Endpoint, "HOST")] = "0.0.0.0";
            environment[ResourceEnvironment.Endpoint(binding.Endpoint, "PORT")] =
                binding.ContainerPort.ToString(CultureInfo.InvariantCulture);
            environment[ResourceEnvironment.Endpoint(binding.Endpoint, "SCHEME")] =
                FindScheme(plan, binding.Endpoint);
            environment.Remove(ResourceEnvironment.Endpoint(binding.Endpoint, "PUBLIC_URL"));
        }

        IReadOnlyDictionary<string, string> publicEndpoints =
            CreatePublicEndpointEnvironment(plan, ownEndpoints);
        foreach ((string key, string value) in publicEndpoints)
        {
            environment[key] = value;
        }

        ApplyDependencyEnvironment(dependencies, environment);

        return environment;
    }

    internal static IReadOnlyDictionary<string, string> CreatePublicEndpointEnvironment(
        ResourcePlan plan,
        IReadOnlyList<ResourceEndpoint> ownEndpoints)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ownEndpoints);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int exposureIndex = 0; exposureIndex < plan.Exposures.Count; exposureIndex++)
        {
            ExposureSpec exposure = plan.Exposures[exposureIndex];
            ResourceEndpoint? selected = null;
            for (int endpointIndex = 0; endpointIndex < ownEndpoints.Count; endpointIndex++)
            {
                ResourceEndpoint endpoint = ownEndpoints[endpointIndex];
                if (!endpoint.IsPublic
                    || !string.Equals(endpoint.Name, exposure.Endpoint, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selected is ResourceEndpoint previous && previous != endpoint)
                {
                    throw new InvalidOperationException(
                        $"Kubernetes exposure '{exposure.Name}' observed more than one public " +
                        $"address for endpoint '{exposure.Endpoint}'.");
                }

                selected = endpoint;
            }

            if (selected is not ResourceEndpoint observed)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(observed.Host)
                || string.IsNullOrWhiteSpace(observed.Scheme)
                || observed.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"Kubernetes exposure '{exposure.Name}' observed an incomplete public " +
                    $"address for endpoint '{exposure.Endpoint}'.");
            }

            Uri address = Uri.CreateEndpoint(observed.Scheme, observed.Host, observed.Port);
            environment[ResourceEnvironment.Endpoint(exposure.Endpoint, "PUBLIC_URL")] =
                address.ToEndpointString();
        }

        return environment;
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
                string dependencyName = dependency.Resource.ToString();
                string urlVariable = ResourceEnvironment.Dependency(dependencyName, endpointName, "URL");
                var projection = new DependencyProjection(
                    dependency.Application,
                    dependency.Resource,
                    endpointName,
                    endpoint);

                if (projected.TryGetValue(urlVariable, out DependencyProjection previous))
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

                projected.Add(urlVariable, projection);
            }
        }

        foreach (DependencyProjection projection in projected.Values)
        {
            string dependencyName = projection.Resource.ToString();
            RemoveDependencyEndpoint(environment, dependencyName, projection.EndpointName);
            if (projection.Endpoint is ResourceEndpoint endpoint)
            {
                ApplyDependencyEndpoint(environment, dependencyName, endpoint);
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

    private static void RemoveDependencyEndpoint(
        IDictionary<string, string> environment,
        string dependency,
        string endpoint)
    {
        environment.Remove(ResourceEnvironment.Dependency(dependency, endpoint, "URL"));
        environment.Remove(ResourceEnvironment.Dependency(dependency, endpoint, "HOST"));
        environment.Remove(ResourceEnvironment.Dependency(dependency, endpoint, "PORT"));
        environment.Remove(ResourceEnvironment.Dependency(dependency, endpoint, "SCHEME"));
    }

    private static void ApplyDependencyEndpoint(
        IDictionary<string, string> environment,
        string dependency,
        ResourceEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host)
            || string.IsNullOrWhiteSpace(endpoint.Scheme)
            || endpoint.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Dependency '{dependency}' reached Running without a complete observed address " +
                $"for endpoint '{endpoint.Name}'.");
        }

        Uri address = Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host, endpoint.Port);
        environment[ResourceEnvironment.Dependency(dependency, endpoint.Name, "URL")] =
            address.ToEndpointString();
        environment[ResourceEnvironment.Dependency(dependency, endpoint.Name, "HOST")] = address.IdnHost;
        environment[ResourceEnvironment.Dependency(dependency, endpoint.Name, "PORT")] =
            address.Port.ToString(CultureInfo.InvariantCulture);
        environment[ResourceEnvironment.Dependency(dependency, endpoint.Name, "SCHEME")] = address.Scheme;
    }

    private static Dictionary<string, byte[]> CreateMountData(
        ResourcePlan plan,
        ResourceInputs inputs,
        ResourceMountKind kind,
        bool preview)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding binding = plan.Container.Mounts[index];
            if (binding.Kind != kind)
            {
                continue;
            }

            if (!inputs.Mounts.TryGetValue(binding.Mount, out ResourceMountInput? input))
            {
                if (preview)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Resolved inputs do not contain planned {kind} mount '{binding.Mount}'.");
            }

            if (!input.IsResolved)
            {
                if (preview)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Mount '{binding.Mount}' is unresolved: {input.UnresolvedReason}");
            }

            data.Add(binding.Mount, input.Content.ToArray());
        }

        return data;
    }

    private static V1Service CreateService(
        ResourcePlan plan,
        ServiceSpec service,
        string namespaceName,
        string resource,
        string planHash,
        string owner)
    {
        ExposureSpec? exposure = null;
        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            if (string.Equals(plan.Exposures[index].Service, service.Name, StringComparison.Ordinal))
            {
                exposure = plan.Exposures[index];
                break;
            }
        }

        var ports = new List<V1ServicePort>();
        if (service.Port is int port)
        {
            PortBinding binding = FindPort(plan.Container.Ports, service.Endpoint!);
            ports.Add(new V1ServicePort
            {
                Name = service.Endpoint,
                Port = exposure?.Port ?? port,
                Protocol = (exposure?.Protocol ?? service.Protocol).ToUpperInvariant(),
                TargetPort = binding.ContainerPort,
            });
        }

        return new V1Service
        {
            ApiVersion = V1Service.KubeApiVersion,
            Kind = V1Service.KubeKind,
            Metadata = KubernetesMetadata.CreateObjectMeta(
                service.Name,
                namespaceName,
                resource,
                planHash,
                owner),
            Spec = new V1ServiceSpec
            {
                ClusterIP = service.Headless ? "None" : null,
                PublishNotReadyAddresses = service.Governing,
                Selector = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesMetadata.ResourceLabel] = resource,
                },
                Ports = ports,
                Type = exposure is not null ? "LoadBalancer" : "ClusterIP",
            },
        };
    }

    private static V1PersistentVolumeClaim CreateClaim(
        VolumeSpec volume,
        string namespaceName,
        string resource,
        string planHash,
        string owner,
        bool includeNamespace)
    {
        V1ObjectMeta metadata = KubernetesMetadata.CreateObjectMeta(
            volume.Name,
            namespaceName,
            resource,
            planHash,
            owner);
        if (!includeNamespace)
        {
            metadata.NamespaceProperty = null;
        }

        return new V1PersistentVolumeClaim
        {
            ApiVersion = V1PersistentVolumeClaim.KubeApiVersion,
            Kind = V1PersistentVolumeClaim.KubeKind,
            Metadata = metadata,
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
                    {
                        ["storage"] = new ResourceQuantity(volume.Size),
                    },
                },
            },
        };
    }

    private static IKubernetesObject<V1ObjectMeta> CreateWorkload(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        string namespaceName,
        string resource,
        string planHash,
        string owner,
        string configMapName,
        string secretName,
        IReadOnlyList<V1PersistentVolumeClaim> claims,
        IReadOnlyList<V1KeyToPath> bootstrapItems,
        string restartPolicy)
    {
        var selector = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KubernetesMetadata.ResourceLabel] = resource,
        };
        V1PodTemplateSpec template = CreatePodTemplate(
            plan,
            artifact,
            selector,
            planHash,
            owner,
            configMapName,
            secretName,
            bootstrapItems,
            restartPolicy);
        V1ObjectMeta metadata = KubernetesMetadata.CreateObjectMeta(
            resource,
            namespaceName,
            resource,
            planHash,
            owner);
        var labelSelector = new V1LabelSelector { MatchLabels = selector };

        return plan.Workload.Kind switch
        {
            WorkloadKind.Deployment => new V1Deployment
            {
                ApiVersion = $"{V1Deployment.KubeGroup}/{V1Deployment.KubeApiVersion}",
                Kind = V1Deployment.KubeKind,
                Metadata = metadata,
                Spec = new V1DeploymentSpec
                {
                    Replicas = plan.Workload.Replicas,
                    Selector = labelSelector,
                    Template = template,
                },
            },
            WorkloadKind.StatefulSet => new V1StatefulSet
            {
                ApiVersion = $"{V1StatefulSet.KubeGroup}/{V1StatefulSet.KubeApiVersion}",
                Kind = V1StatefulSet.KubeKind,
                Metadata = metadata,
                Spec = new V1StatefulSetSpec
                {
                    Replicas = plan.Workload.Replicas,
                    Selector = labelSelector,
                    ServiceName = FindGoverningService(plan.Services).Name,
                    Template = template,
                    VolumeClaimTemplates = new List<V1PersistentVolumeClaim>(claims),
                },
            },
            WorkloadKind.DaemonSet => new V1DaemonSet
            {
                ApiVersion = $"{V1DaemonSet.KubeGroup}/{V1DaemonSet.KubeApiVersion}",
                Kind = V1DaemonSet.KubeKind,
                Metadata = metadata,
                Spec = new V1DaemonSetSpec
                {
                    Selector = labelSelector,
                    Template = template,
                },
            },
            WorkloadKind.Job => new V1Job
            {
                ApiVersion = $"{V1Job.KubeGroup}/{V1Job.KubeApiVersion}",
                Kind = V1Job.KubeKind,
                Metadata = metadata,
                Spec = new V1JobSpec
                {
                    BackoffLimit = 0,
                    Completions = plan.Workload.Replicas,
                    Parallelism = plan.Workload.Replicas,
                    Template = template,
                },
            },
            _ => throw new InvalidDataException($"Unknown workload kind '{plan.Workload.Kind}'."),
        };
    }

    private static V1PodTemplateSpec CreatePodTemplate(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        Dictionary<string, string> selector,
        string planHash,
        string owner,
        string configMapName,
        string secretName,
        IReadOnlyList<V1KeyToPath> bootstrapItems,
        string restartPolicy)
    {
        var ports = new List<V1ContainerPort>();
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding binding = plan.Container.Ports[index];
            ports.Add(new V1ContainerPort
            {
                Name = KubernetesMetadata.RequireDnsLabel(binding.Endpoint, "container port name"),
                ContainerPort = binding.ContainerPort,
                Protocol = binding.Protocol.ToUpperInvariant(),
            });
        }

        var volumes = new List<V1Volume>
        {
            new()
            {
                Name = ConfigurationVolumeName,
                ConfigMap = new V1ConfigMapVolumeSource { Name = configMapName },
            },
            new()
            {
                Name = SecretVolumeName,
                Secret = new V1SecretVolumeSource { SecretName = secretName },
            },
        };
        var volumeMounts = new List<V1VolumeMount>();
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding mount = plan.Container.Mounts[index];
            if (mount.Kind is ResourceMountKind.Volume)
            {
                if (plan.Workload.Kind is not WorkloadKind.StatefulSet)
                {
                    volumes.Add(new V1Volume
                    {
                        Name = mount.Mount,
                        PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                        {
                            ClaimName = mount.Mount,
                        },
                    });
                }

                volumeMounts.Add(new V1VolumeMount
                {
                    Name = mount.Mount,
                    MountPath = mount.ContainerPath,
                });
                continue;
            }

            volumeMounts.Add(new V1VolumeMount
            {
                Name = mount.Kind is ResourceMountKind.Configuration
                    ? ConfigurationVolumeName
                    : SecretVolumeName,
                MountPath = mount.ContainerPath,
                SubPath = mount.Mount,
                ReadOnlyProperty = true,
            });
        }

        if (bootstrapItems.Count > 0)
        {
            volumes.Add(new V1Volume
            {
                Name = BootstrapVolumeName,
                Projected = new V1ProjectedVolumeSource
                {
                    DefaultMode = 256,
                    Sources =
                    [
                        new V1VolumeProjection
                        {
                            Secret = new V1SecretProjection
                            {
                                Name = secretName,
                                Items = new List<V1KeyToPath>(bootstrapItems),
                            },
                        },
                    ],
                },
            });
            volumeMounts.Add(new V1VolumeMount
            {
                Name = BootstrapVolumeName,
                MountPath = BootstrapDirectory,
                ReadOnlyProperty = true,
            });
        }

        var container = new V1Container
        {
            Name = plan.Container.Name,
            Image = $"{artifact.Repository}@{artifact.Digest}",
            ImagePullPolicy = "IfNotPresent",
            EnvFrom =
            [
                new V1EnvFromSource
                {
                    ConfigMapRef = new V1ConfigMapEnvSource { Name = configMapName },
                },
            ],
            Ports = ports,
            VolumeMounts = volumeMounts,
        };
        ApplyProbes(container, plan);

        var podLabels = new Dictionary<string, string>(selector, StringComparer.Ordinal)
        {
            [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue,
        };
        var podAnnotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KubernetesMetadata.PlanHashAnnotation] = planHash,
            [KubernetesMetadata.OwnerAnnotation] = owner,
        };
        return new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Labels = podLabels,
                Annotations = podAnnotations,
            },
            Spec = new V1PodSpec
            {
                Containers = [container],
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = 1654,
                    RunAsGroup = 1654,
                    FsGroup = 1654,
                },
                RestartPolicy = restartPolicy,
                TerminationGracePeriodSeconds = plan.Workload.StopGraceSeconds,
                Volumes = volumes,
            },
        };
    }

    private static void ApplyProbes(
        V1Container container,
        ResourcePlan plan)
    {
        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping mapping = plan.Container.Probes[index];
            V1Probe? probe = CreateProbe(mapping, plan);
            switch (mapping.Role)
            {
                case "readiness":
                    container.ReadinessProbe = probe;
                    break;
                case "liveness":
                    container.LivenessProbe = probe;
                    break;
                case "startup":
                    container.StartupProbe = probe;
                    break;
                default:
                    throw new InvalidDataException($"Unknown probe role '{mapping.Role}'.");
            }
        }

        if (NeedsControlPlaneReadiness(plan))
        {
            container.ReadinessProbe = new V1Probe
            {
                HttpGet = new V1HTTPGetAction
                {
                    Path = plan.ControlPlane.Path,
                    Port = FindPort(plan.Container.Ports, plan.ControlPlane.Endpoint).ContainerPort,
                    Scheme = FindHttpScheme(plan, plan.ControlPlane.Endpoint),
                },
            };
        }
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

    private static V1Probe? CreateProbe(
        ProbeMapping mapping,
        ResourcePlan plan)
    {
        if (mapping.Kind is ProbeKind.None)
        {
            return null;
        }

        int port = mapping.Endpoint is null
            ? 0
            : FindPort(plan.Container.Ports, mapping.Endpoint).ContainerPort;
        return mapping.Kind switch
        {
            ProbeKind.Http => new V1Probe
            {
                HttpGet = new V1HTTPGetAction
                {
                    Path = mapping.Value,
                    Port = port,
                    Scheme = FindHttpScheme(plan, mapping.Endpoint!),
                },
            },
            ProbeKind.Tcp => new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = port },
            },
            ProbeKind.Exec => new V1Probe
            {
                Exec = new V1ExecAction { Command = new List<string>(mapping.Command) },
            },
            ProbeKind.Grpc => new V1Probe
            {
                Grpc = new V1GRPCAction { Port = port, Service = mapping.Value },
            },
            _ => throw new InvalidDataException($"Unknown probe kind '{mapping.Kind}'."),
        };
    }

    private static IReadOnlyList<ResourceEndpoint> CreateObservedEndpoints(
        ResourcePlan plan,
        string namespaceName)
    {
        var endpoints = new List<ResourceEndpoint>(plan.Container.Ports.Count);
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding binding = plan.Container.Ports[index];
            ServiceSpec service = FindService(plan.Services, binding.Endpoint);
            string scheme = FindScheme(plan, binding.Endpoint);
            ServiceSpec discoveryService = plan.Workload.Kind is WorkloadKind.StatefulSet
                ? FindGoverningService(plan.Services)
                : service;
            string host = $"{discoveryService.Name}.{namespaceName}.svc";
            _ = new UriBuilder(scheme, host, service.Port ?? binding.ContainerPort).Uri;
            endpoints.Add(new ResourceEndpoint(
                binding.Endpoint,
                scheme,
                service.Port ?? binding.ContainerPort,
                false,
                host));
        }

        return endpoints;
    }

    private static void AddPatched<T>(
        ICollection<IKubernetesObject<V1ObjectMeta>> objects,
        ResourceName resourceName,
        T candidate,
        string name,
        string namespaceName,
        string resource,
        string planHash,
        string owner,
        KubernetesGatewayOptions options)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        options.ApplyPatches(resourceName, candidate);
        candidate.Metadata ??= new V1ObjectMeta();
        KubernetesMetadata.RestoreRequiredMetadata(
            candidate.Metadata,
            name,
            namespaceName,
            resource,
            planHash,
            owner);
        objects.Add(candidate);
    }

    private static void RestorePodMetadata(
        IKubernetesObject<V1ObjectMeta> workload,
        string resource,
        string planHash,
        string owner)
    {
        V1PodTemplateSpec? template = workload switch
        {
            V1Deployment deployment => deployment.Spec?.Template,
            V1StatefulSet statefulSet => statefulSet.Spec?.Template,
            V1DaemonSet daemonSet => daemonSet.Spec?.Template,
            V1Job job => job.Spec?.Template,
            _ => null,
        };
        if (template is null)
        {
            throw new InvalidOperationException("A Kubernetes workload patch removed the required pod template.");
        }

        template.Metadata ??= new V1ObjectMeta();
        template.Metadata.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        template.Metadata.Annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
        template.Metadata.Labels[KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue;
        template.Metadata.Labels[KubernetesMetadata.ResourceLabel] = resource;
        template.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation] = planHash;
        template.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation] = owner;

        V1LabelSelector? selector = workload switch
        {
            V1Deployment deployment => deployment.Spec?.Selector,
            V1StatefulSet statefulSet => statefulSet.Spec?.Selector,
            V1DaemonSet daemonSet => daemonSet.Spec?.Selector,
            _ => null,
        };
        if (selector is not null)
        {
            selector.MatchLabels ??= new Dictionary<string, string>(StringComparer.Ordinal);
            selector.MatchLabels[KubernetesMetadata.ResourceLabel] = resource;
        }
    }

    private static void RestoreStatefulClaimMetadata(
        IKubernetesObject<V1ObjectMeta> workload,
        ResourcePlan plan,
        string namespaceName,
        string resource,
        string planHash,
        string owner)
    {
        if (workload is not V1StatefulSet statefulSet)
        {
            return;
        }

        IList<V1PersistentVolumeClaim>? claims = statefulSet.Spec?.VolumeClaimTemplates;
        if (claims is null || claims.Count != plan.Volumes.Count)
        {
            throw new InvalidOperationException(
                $"A Kubernetes patch changed the StatefulSet claim-template count for resource " +
                $"'{plan.Resource}'. Register claim-template changes as " +
                $"{nameof(V1PersistentVolumeClaim)} patches without changing plan identities.");
        }

        for (int index = 0; index < claims.Count; index++)
        {
            V1PersistentVolumeClaim claim = claims[index]
                ?? throw new InvalidOperationException(
                    $"A Kubernetes patch removed StatefulSet claim template '{plan.Volumes[index].Name}'.");
            string expectedName = plan.Volumes[index].Name;
            if (!string.Equals(claim.Metadata?.Name, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A Kubernetes patch changed StatefulSet claim template '{expectedName}' to " +
                    $"'{claim.Metadata?.Name ?? "<none>"}'. Claim-template identities are plan-owned.");
            }

            claim.Metadata ??= new V1ObjectMeta();
            KubernetesMetadata.RestoreRequiredMetadata(
                claim.Metadata,
                expectedName,
                namespaceName,
                resource,
                planHash,
                owner);
            claim.Metadata.NamespaceProperty = null;
        }
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
            if (string.Equals(plan.Exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return plan.Exposures[index].Scheme;
            }
        }

        return string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase)
            ? "udp"
            : "tcp";
    }

    private static string ResolveRestartPolicy(ResourcePlan plan, ICollection<string> warnings)
    {
        string requested = plan.Workload.RestartPolicy;
        if (plan.Workload.Kind is WorkloadKind.Job)
        {
            return requested is "Never" or "OnFailure" ? requested : "Never";
        }

        if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, "Always", StringComparison.Ordinal))
        {
            warnings.Add(
                $"Kubernetes workload '{plan.Resource}' ({plan.Workload.Kind}) requires restart policy 'Always'; " +
                $"the requested '{requested}' policy was normalized to 'Always'.");
        }

        return "Always";
    }

    private static string FindHttpScheme(ResourcePlan plan, string endpoint)
    {
        string scheme = FindScheme(plan, endpoint);
        return string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? "HTTPS"
            : "HTTP";
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

        throw new InvalidDataException($"Endpoint '{endpoint}' has no Kubernetes Service in the resource plan.");
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

    private static ServiceSpec FindGoverningService(IReadOnlyList<ServiceSpec> services)
    {
        for (int index = 0; index < services.Count; index++)
        {
            if (services[index].Governing && services[index].Headless)
            {
                return services[index];
            }
        }

        throw new InvalidDataException("A StatefulSet plan must declare a headless governing Service.");
    }

    private static void RequireProtocol(string protocol, string description)
    {
        if (!string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Kubernetes {description} uses unsupported protocol '{protocol}'.");
        }
    }

    private static void RequireEnvironmentName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || !IsEnvironmentInitial(name[0]))
        {
            throw new InvalidDataException(
                $"Kubernetes environment variable name '{name}' must match [A-Za-z_][A-Za-z0-9_]*.");
        }

        for (int index = 1; index < name.Length; index++)
        {
            char character = name[index];
            if (!IsEnvironmentInitial(character)
                && character is not (>= '0' and <= '9'))
            {
                throw new InvalidDataException(
                    $"Kubernetes environment variable name '{name}' must match [A-Za-z_][A-Za-z0-9_]*.");
            }
        }
    }

    private static bool IsEnvironmentInitial(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    private static void RequireDataKey(string key)
    {
        if (key.Length is 0 or > 253)
        {
            throw new InvalidDataException($"Mount name '{key}' is not a valid ConfigMap or Secret key.");
        }

        for (int index = 0; index < key.Length; index++)
        {
            char character = key[index];
            if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or
                (>= '0' and <= '9') or '-' or '_' or '.')
            {
                continue;
            }

            throw new InvalidDataException($"Mount name '{key}' is not a valid ConfigMap or Secret key.");
        }
    }

    private static void RequireDisjointKeys(
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData)
    {
        foreach (string key in binaryData.Keys)
        {
            if (data.ContainsKey(key))
            {
                throw new InvalidDataException(
                    $"Configuration mount '{key}' conflicts with a ConfigMap environment key.");
            }
        }
    }

    private static bool SetEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var values = new HashSet<T>(left);
        return values.Count == left.Count && values.SetEquals(right);
    }

    private readonly record struct DependencyProjection(
        ApplicationName Application,
        ResourceName Resource,
        string EndpointName,
        ResourceEndpoint? Endpoint);
}
