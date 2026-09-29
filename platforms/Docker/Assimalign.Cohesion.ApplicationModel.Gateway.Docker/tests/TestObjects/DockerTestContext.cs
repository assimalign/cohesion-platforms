using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

internal sealed class DockerTestContext : IResourceControlContext
{
    private readonly IContainerImageArtifact _artifact;

    private DockerTestContext(
        DockerTestResource resource,
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        string owner)
    {
        Resource = resource;
        Plan = plan;
        _artifact = artifact;
        Inputs = inputs;
        Model = new DockerTestModel(resource, plan, owner);
    }

    public IApplicationResourceDescriptor Descriptor => null!;

    public IApplicationResource Resource { get; }

    public ResourcePlan Plan { get; }

    public IApplicationModel Model { get; }

    public IApplicationResourceStateManager State { get; } = new InMemoryResourceStateManager();

    public IReadOnlyList<IApplicationResource> Dependencies => [];

    public ResourceInputs Inputs { get; }

    public IReadOnlyList<ResourceDependencyObservation> ObservedDependencies => [];

    public T GetArtifact<T>() where T : class, IResourceArtifact => (T)_artifact;

    public static DockerTestContext Create(
        IReadOnlyList<ProbeMapping>? probes = null,
        bool includeVolume = false,
        bool includeInputs = false,
        bool expose = false,
        RestartPolicy restartPolicy = RestartPolicy.OnFailure,
        WorkloadKind workload = WorkloadKind.Deployment,
        string settingsContent = "{\"enabled\":true}")
    {
        const string resourceName = "worker";
        var resource = new DockerTestResource(resourceName, restartPolicy, workload);
        var mounts = new List<MountBinding>();
        var volumes = new List<VolumeSpec>();
        if (includeVolume)
        {
            mounts.Add(new MountBinding(
                "data",
                "/var/lib/worker",
                ResourceMountKind.Volume,
                null));
            volumes.Add(new VolumeSpec(
                "data",
                ResourceMountKind.Volume,
                "1Gi",
                PerReplicaClaim: true));
        }

        var inputs = new Dictionary<string, ResourceMountInput>(StringComparer.Ordinal);
        ReadOnlyMemory<byte> bootstrap = ReadOnlyMemory<byte>.Empty;
        if (includeInputs)
        {
            mounts.Add(new MountBinding(
                "settings",
                "/app/settings.json",
                ResourceMountKind.Configuration,
                "parameter:settings"));
            mounts.Add(new MountBinding(
                "api-key",
                "/run/secrets/api-key",
                ResourceMountKind.Secret,
                "secret:api-key"));
            inputs.Add(
                "settings",
                ResourceMountInput.Resolved(
                    "parameter:settings",
                    Encoding.UTF8.GetBytes(settingsContent)));
            inputs.Add(
                "api-key",
                ResourceMountInput.Resolved(
                    "secret:api-key",
                    Encoding.UTF8.GetBytes("top-secret")));
            bootstrap = Encoding.UTF8.GetBytes("bootstrap-token");
        }

        IReadOnlyList<PortBinding> ports = expose
            ? [new PortBinding("http", 8080, "tcp")]
            : [];
        IReadOnlyList<ServiceSpec> services = expose
            ? [new ServiceSpec("worker-http", "http", 8080, "tcp", false, false)]
            : [];
        IReadOnlyList<ExposureSpec> exposures = expose
            ? [new ExposureSpec("worker-public", "http", "worker-http", "http", "tcp", 8080)]
            : [];
        var plan = new ResourcePlan(
            ResourcePlan.CurrentSchema,
            resourceName,
            "Worker",
            new WorkloadSpec(
                workload,
                1,
                workload is WorkloadKind.StatefulSet,
                ReadinessGate.For(workload),
                30),
            new ContainerSpec(
                resourceName,
                ArtifactRef.Self,
                ports,
                mounts,
                new Dictionary<string, string>
                {
                    ["WORKER_MODE"] = "test",
                },
                probes ?? []),
            volumes,
            services,
            exposures,
            new Dictionary<string, string>());
        string digest = $"sha256:{new string('a', 64)}";
        var artifact = new DockerImageArtifact(
            ((IApplicationResource)resource).Id,
            "registry.example/worker",
            digest,
            null,
            $"sha256:{new string('b', 64)}");
        return new DockerTestContext(
            resource,
            plan,
            artifact,
            new ResourceInputs(inputs, bootstrap),
            "appa@docker");
    }
}

internal sealed class DockerTestResource : IManifestResource
{
    public DockerTestResource(
        string name,
        RestartPolicy restartPolicy,
        WorkloadKind workload)
    {
        Name = name;
        Manifest = new ResourceManifest
        {
            Name = name,
            Kind = "Worker",
            Application = "appa",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Assimalign.Cohesion.Test.Application",
                Image = $"registry.example/worker@sha256:{new string('a', 64)}",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = workload,
                RestartPolicy = restartPolicy.ToString(),
                ExitCodes = "cohesion/sysexits/v1",
            },
        };
    }

    public ResourceName Name { get; }

    public ResourceManifest Manifest { get; }
}

internal sealed class DockerTestModel : IApplicationModel
{
    public DockerTestModel(
        IApplicationResource resource,
        ResourcePlan plan,
        string owner)
    {
        Resources = [resource];
        Plans = [plan];
        Owner = owner;
    }

    public ApplicationName Name => "appa";

    public IApplicationEnvironment Environment { get; } = new DockerTestEnvironment();

    public GatewayRunMode RunMode => GatewayRunMode.Run;

    public ResourceName GatewayIdentity => "docker";

    public string Owner { get; }

    public bool Adopt => false;

    public bool RestartOrphans => false;

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => [];

    public IReadOnlyList<IApplicationResource> Resources { get; }

    public IReadOnlyList<ResourceManifest> Manifests => [];

    public IReadOnlyList<ResourcePlan> Plans { get; }
}

internal sealed class DockerTestEnvironment : IApplicationEnvironment
{
    public EnvironmentName Name => "Local";

    public bool IsLocal => true;

    public bool IsDevelopment => false;
}
