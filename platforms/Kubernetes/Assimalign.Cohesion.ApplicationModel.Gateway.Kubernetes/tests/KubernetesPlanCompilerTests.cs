using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

using k8s.Models;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesPlanCompilerTests
{
    private static readonly string Image = $"registry.example/test@sha256:{new string('a', 64)}";

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Compiler: Should create the exact KindMatrix workload")]
    [InlineData("web", WorkloadKind.Deployment, "Deployment")]
    [InlineData("database", WorkloadKind.StatefulSet, "StatefulSet")]
    [InlineData("generic-volume", WorkloadKind.StatefulSet, "StatefulSet")]
    [InlineData("daemon-set", WorkloadKind.DaemonSet, "DaemonSet")]
    [InlineData("job", WorkloadKind.Job, "Job")]
    public void Compile_OnKindMatrixShape_ShouldCreateExactWorkload(
        string shape, WorkloadKind expectedKind, string expectedKubernetesKind)
    {
        PlanCase planCase = CreateCase(shape);

        KubernetesPlanCompilation result = Compile(planCase);

        result.Objects.Count(item => item.Kind == expectedKubernetesKind).ShouldBe(1);
        result.Readiness.Kind.ShouldBe(expectedKind);
        result.Objects.ShouldAllBe(item =>
            item.Metadata.Labels[KubernetesMetadata.ResourceLabel] == planCase.Plan.Resource.Value
            && item.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation] == result.PlanHash);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Render: Should be deterministic for every KindMatrix shape")]
    [InlineData("web", "ConfigMap,Secret,Service,Service,Deployment")]
    [InlineData("database", "ConfigMap,Secret,Service,Service,Service,StatefulSet")]
    [InlineData("generic-volume", "ConfigMap,Secret,Service,Service,StatefulSet")]
    [InlineData("daemon-set", "ConfigMap,Secret,Service,DaemonSet")]
    [InlineData("job", "ConfigMap,Secret,Service,Job")]
    public void Render_OnKindMatrixShape_ShouldBeDeterministic(
        string shape,
        string expectedObjectKinds)
    {
        PlanCase planCase = CreateCase(shape);

        string first = KubernetesPlanRenderer.Render(Compile(planCase));
        string second = KubernetesPlanRenderer.Render(Compile(planCase));
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "render",
            $"{shape}.yaml");

        first.ShouldBe(second);
        first.ShouldBe(File.ReadAllText(fixture).Replace("\r\n", "\n", StringComparison.Ordinal));
        first.ShouldContain(planCase.Plan.Resource.Value);
        string.Join(',', Compile(planCase).Objects.Select(item => item.Kind))
            .ShouldBe(expectedObjectKinds);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Compiler: Should create a StatefulSet claim template and headless Service")]
    public void Compile_OnStatefulVolumePlan_ShouldCreateClaimTemplateAndHeadlessService()
    {
        PlanCase planCase = CreateCase("database");

        KubernetesPlanCompilation result = Compile(planCase);

        V1StatefulSet workload = result.Objects.OfType<V1StatefulSet>().Single();
        V1PersistentVolumeClaim claim = workload.Spec.VolumeClaimTemplates.Single();
        claim.Metadata.Name.ShouldBe("data");
        claim.Spec.Resources.Requests["storage"].ToString().ShouldBe("10Gi");
        workload.Spec.Template.Spec.Volumes
            .ShouldNotContain(volume => volume.Name == "data");
        workload.Spec.Template.Spec.Containers.Single().VolumeMounts
            .ShouldContain(mount => mount.Name == "data" && mount.MountPath == "/var/lib/database");
        workload.Spec.ServiceName.ShouldBe("appa-database-headless");
        result.Objects.OfType<V1PersistentVolumeClaim>().ShouldBeEmpty();
        V1Service governing = result.Objects.OfType<V1Service>()
            .Single(service => service.Metadata.Name == "appa-database-headless");
        governing.Spec.ClusterIP.ShouldBe("None");
        governing.Spec.PublishNotReadyAddresses.ShouldBe(true);
        result.Endpoints.ShouldAllBe(endpoint =>
            endpoint.Host == "appa-database-headless.appa.svc");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should bind pod endpoints on all interfaces")]
    public void Compile_OnContainerEndpoints_ShouldUsePodBindAddress()
    {
        KubernetesPlanCompilation result = Compile(CreateCase("web"));
        V1ConfigMap configuration = result.Objects.OfType<V1ConfigMap>().Single();

        configuration.Data[ResourceEnvironment.Endpoint("http", "HOST")].ShouldBe("0.0.0.0");
        result.Endpoints.Single(endpoint => endpoint.Name == "http").Host
            .ShouldBe("appa-api-http.appa.svc");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should inject an observed public exposure URL")]
    public void Compile_OnObservedPublicExposure_ShouldWritePublicUrl()
    {
        KubernetesPlanCompilation result = Compile(
            CreateCase("web"),
            ownEndpoints:
            [
                new ResourceEndpoint(
                    "https",
                    "https",
                    8443,
                    IsPublic: true,
                    Host: "api.example.test"),
            ]);
        V1ConfigMap configuration = result.Objects.OfType<V1ConfigMap>().Single();

        configuration.Data[ResourceEnvironment.Endpoint("https", "PUBLIC_URL")]
            .ShouldBe("https://api.example.test:8443");
        configuration.Data.ShouldNotContainKey(
            ResourceEnvironment.Endpoint("http", "PUBLIC_URL"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Compiler: Should map declared probes one-to-one")]
    public void Compile_OnExplicitProbePlan_ShouldMapProbesOneToOne()
    {
        V1Deployment workload = Compile(CreateCase("web")).Objects.OfType<V1Deployment>().Single();
        V1Container container = workload.Spec.Template.Spec.Containers.Single();

        container.ReadinessProbe.HttpGet.Path.ShouldBe("/readyz");
        container.ReadinessProbe.HttpGet.Port.Value.ShouldBe("8443");
        container.ReadinessProbe.HttpGet.Scheme.ShouldBe("HTTPS");
        container.LivenessProbe.TcpSocket.Port.Value.ShouldBe("8080");
        container.StartupProbe.ShouldBeNull();
        workload.Spec.Template.Spec.TerminationGracePeriodSeconds.ShouldBe(30);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Plan hash: Should ignore bootstrap credential rotation")]
    public void Compile_OnRotatedBootstrapCredential_ShouldKeepPlanHashStable()
    {
        PlanCase planCase = CreateCase("web");
        var firstInputs = new ResourceInputs(new Dictionary<string, ResourceMountInput>(), Encoding.UTF8.GetBytes("first"));
        var secondInputs = new ResourceInputs(new Dictionary<string, ResourceMountInput>(), Encoding.UTF8.GetBytes("second"));

        KubernetesPlanCompilation first = Compile(planCase, firstInputs);
        KubernetesPlanCompilation second = Compile(planCase, secondInputs);

        first.PlanHash.ShouldBe(second.PlanHash);
        first.Objects.OfType<V1Secret>().Single().Data["bootstrap-token"]
            .ShouldNotBe(second.Objects.OfType<V1Secret>().Single().Data["bootstrap-token"]);
        V1Deployment workload = first.Objects.OfType<V1Deployment>().Single();
        V1Volume bootstrapVolume = workload.Spec.Template.Spec.Volumes
            .Single(volume => volume.Name == "cohesion-bootstrap");
        V1SecretProjection bootstrapProjection = bootstrapVolume.Projected.Sources
            .Single().Secret;
        V1KeyToPath bootstrapItem = bootstrapProjection.Items.Single();
        V1VolumeMount bootstrapMount = workload.Spec.Template.Spec.Containers.Single()
            .VolumeMounts.Single(mount => mount.Name == "cohesion-bootstrap");

        bootstrapProjection.Name.ShouldBe("appa-api-secret");
        bootstrapVolume.Projected.DefaultMode.ShouldBe(256);
        bootstrapItem.Key.ShouldBe("bootstrap-token");
        bootstrapItem.Path.ShouldBe("bootstrap.token");
        bootstrapMount.MountPath.ShouldBe("/var/run/cohesion");
        bootstrapMount.SubPath.ShouldBeNull();
        first.Objects.OfType<V1ConfigMap>().Single().Data[ResourceEnvironment.BootstrapTokenPath]
            .ShouldBe("/var/run/cohesion/bootstrap.token");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Plan hash: Should canonicalize map insertion order")]
    public void ComputePlanHash_OnEquivalentMapOrders_ShouldMatch()
    {
        ResourcePlan original = CreateCase("web").Plan;
        var reorderedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in original.Container.Environment.Reverse())
        {
            reorderedEnvironment.Add(key, value);
        }

        var reorderedHints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in original.Hints.Reverse())
        {
            reorderedHints.Add(key, value);
        }

        var container = new ContainerSpec(
            original.Container.Name,
            original.Container.Artifact,
            original.Container.Ports,
            original.Container.Mounts,
            reorderedEnvironment,
            original.Container.Probes);
        ResourcePlan reordered = CopyPlan(
            original,
            hints: reorderedHints,
            container: container);

        KubernetesPlanCompiler.ComputePlanHash(reordered)
            .ShouldBe(KubernetesPlanCompiler.ComputePlanHash(original));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Inputs: Should project configuration and secret mounts without environment leakage")]
    public void Compile_OnResolvedMountInputs_ShouldProjectTypedDataVolumes()
    {
        ResourcePlan plan = CreatePlan(
            "configured-worker",
            "Worker",
            WorkloadKind.Deployment,
            1,
            [],
            [
                new("settings", "/app/settings.json", ResourceMountKind.Configuration, "parameter:settings"),
                new("password", "/run/secrets/password", ResourceMountKind.Secret, "secret:password"),
            ],
            [],
            [],
            [],
            []);
        var inputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>
            {
                ["settings"] = ResourceMountInput.Resolved("parameter:settings", Encoding.UTF8.GetBytes("{}")),
                ["password"] = ResourceMountInput.Resolved("secret:password", Encoding.UTF8.GetBytes("private")),
            },
            Encoding.UTF8.GetBytes("bootstrap"));

        KubernetesPlanCompilation result = Compile(
            new PlanCase(plan, CreateManifest(plan)),
            inputs);

        result.Objects.OfType<V1ConfigMap>().Single().BinaryData["settings"]
            .ShouldBe(Encoding.UTF8.GetBytes("{}"));
        V1Secret secret = result.Objects.OfType<V1Secret>().Single();
        secret.Data["password"].ShouldBe(Encoding.UTF8.GetBytes("private"));
        secret.Data["bootstrap-token"].ShouldBe(Encoding.UTF8.GetBytes("bootstrap"));
        result.Objects.OfType<V1Deployment>().Single().Spec.Template.Spec.Containers.Single()
            .VolumeMounts.Select(mount => mount.SubPath)
            .ShouldContain("password");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Inputs: Should reject ConfigMap data and binary-data key collisions")]
    public void Compile_OnConfigurationMountCollidingWithEnvironment_ShouldRejectInput()
    {
        // Arrange
        ResourcePlan plan = CreatePlan(
            "worker",
            "Worker",
            WorkloadKind.Deployment,
            1,
            [],
            [new MountBinding("COHESION_GATEWAY", "/app/gateway", ResourceMountKind.Configuration, "literal:value")],
            [],
            [],
            [],
            []);
        var inputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>
            {
                ["COHESION_GATEWAY"] = ResourceMountInput.Resolved(
                    "literal:value",
                    Encoding.UTF8.GetBytes("value")),
            },
            ReadOnlyMemory<byte>.Empty);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(new PlanCase(plan, CreateManifest(plan)), inputs));

        // Assert
        exception.Message.ShouldContain("conflicts with a ConfigMap environment key");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Inputs: Should reserve the bootstrap Secret key")]
    public void Compile_OnSecretMountUsingReservedBootstrapKey_ShouldRejectInput()
    {
        // Arrange
        ResourcePlan plan = CreatePlan(
            "worker",
            "Worker",
            WorkloadKind.Deployment,
            1,
            [],
            [new MountBinding("bootstrap-token", "/run/user-token", ResourceMountKind.Secret, "secret:token")],
            [],
            [],
            [],
            []);
        var inputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>
            {
                ["bootstrap-token"] = ResourceMountInput.Resolved(
                    "secret:token",
                    Encoding.UTF8.GetBytes("user-token")),
            },
            ReadOnlyMemory<byte>.Empty);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(new PlanCase(plan, CreateManifest(plan)), inputs));

        // Assert
        exception.Message.ShouldContain("reserved gateway bootstrap credential key");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Dependencies: Should project only requested endpoints and prefer internal Service DNS")]
    public void Compile_OnObservedDependencyEndpoints_ShouldProjectRequestedInternalEndpoint()
    {
        ResourceDependencyObservation dependency = new(
            "appa",
            "orders",
            ResourceLifecycle.Degraded,
            ["http"],
            [
                new ResourceEndpoint("metrics", "http", 9090, false, "orders-metrics.appa.svc"),
                new ResourceEndpoint("http", "https", 443, true, "orders.example.test"),
                new ResourceEndpoint("http", "http", 8080, false, "orders-http.appa.svc"),
            ],
            optional: true);

        KubernetesPlanCompilation result = Compile(
            CreateCase("web"),
            dependencies: [dependency]);
        V1ConfigMap configuration = result.Objects.OfType<V1ConfigMap>().Single();

        configuration.Data[ResourceEnvironment.Dependency("orders", "http", "URL")]
            .ShouldBe("http://orders-http.appa.svc:8080");
        configuration.Data[ResourceEnvironment.Dependency("orders", "http", "HOST")]
            .ShouldBe("orders-http.appa.svc");
        configuration.Data[ResourceEnvironment.Dependency("orders", "http", "PORT")]
            .ShouldBe("8080");
        configuration.Data[ResourceEnvironment.Dependency("orders", "http", "SCHEME")]
            .ShouldBe("http");
        configuration.Data.ShouldNotContainKey(
            ResourceEnvironment.Dependency("orders", "metrics", "URL"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Dependencies: Should remove unavailable optional endpoint values")]
    public void Compile_OnUnavailableOptionalDependency_ShouldRemoveProjectedEndpoint()
    {
        PlanCase valid = CreateCase("web");
        string[] variables = DependencyVariables("orders", "http");
        var environment = new Dictionary<string, string>(valid.Plan.Container.Environment, StringComparer.Ordinal);
        for (int index = 0; index < variables.Length; index++)
        {
            environment[variables[index]] = "spoofed";
        }

        var container = new ContainerSpec(
            valid.Plan.Container.Name,
            valid.Plan.Container.Artifact,
            valid.Plan.Container.Ports,
            valid.Plan.Container.Mounts,
            environment,
            valid.Plan.Container.Probes);
        ResourcePlan plan = CopyPlan(valid.Plan, container: container);
        ResourceDependencyObservation dependency = new(
            "appa",
            "orders",
            ResourceLifecycle.Pending,
            ["http"],
            [],
            optional: true);

        KubernetesPlanCompilation result = Compile(
            valid with { Plan = plan },
            dependencies: [dependency]);
        V1ConfigMap configuration = result.Objects.OfType<V1ConfigMap>().Single();

        for (int index = 0; index < variables.Length; index++)
        {
            configuration.Data.ShouldNotContainKey(variables[index]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Dependencies: Should reject normalized environment-name collisions")]
    public void Compile_OnCollidingDependencyEndpointNames_ShouldRejectProjection()
    {
        ResourceDependencyObservation dependency = new(
            "appa",
            "orders",
            ResourceLifecycle.Running,
            ["admin-http", "admin.http"],
            [
                new ResourceEndpoint("admin-http", "http", 8080, false, "orders-admin.appa.svc"),
                new ResourceEndpoint("admin.http", "http", 8081, false, "orders-admin.appa.svc"),
            ],
            optional: false);

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(CreateCase("web"), dependencies: [dependency]));

        exception.Message.ShouldContain("after environment-name normalization");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Patches: Should restore mandatory Cohesion metadata")]
    public void Compile_OnPatchRemovingMetadata_ShouldRestoreMandatoryMetadata()
    {
        PlanCase planCase = CreateCase("web");
        var options = new KubernetesGatewayOptions();
        options.Patch<V1Deployment>(planCase.Plan.Resource, deployment =>
        {
            deployment.Metadata.Name = "changed";
            deployment.Metadata.NamespaceProperty = "changed";
            deployment.Metadata.Labels = new Dictionary<string, string> { ["custom"] = "preserved" };
            deployment.Metadata.Annotations = new Dictionary<string, string>();
            deployment.Spec.Template.Metadata.Labels = new Dictionary<string, string>();
            deployment.Spec.Template.Metadata.Annotations = new Dictionary<string, string>();
        });

        KubernetesPlanCompilation result = Compile(planCase, ResourceInputs.Empty, options);
        V1Deployment workload = result.Objects.OfType<V1Deployment>().Single();

        workload.Metadata.Name.ShouldBe(planCase.Plan.Resource.Value);
        workload.Metadata.NamespaceProperty.ShouldBe("appa");
        workload.Metadata.Labels["custom"].ShouldBe("preserved");
        workload.Metadata.Labels[KubernetesMetadata.ResourceLabel].ShouldBe(planCase.Plan.Resource.Value);
        workload.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation].ShouldBe(result.PlanHash);
        workload.Spec.Template.Metadata.Labels[KubernetesMetadata.ResourceLabel].ShouldBe(planCase.Plan.Resource.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Patches: Should reject a changed StatefulSet claim identity")]
    public void Compile_OnParentPatchRenamingClaimTemplate_ShouldReject()
    {
        PlanCase planCase = CreateCase("database");
        var options = new KubernetesGatewayOptions();
        options.Patch<V1StatefulSet>(planCase.Plan.Resource, statefulSet =>
            statefulSet.Spec.VolumeClaimTemplates[0].Metadata.Name = "foreign-data");

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => Compile(planCase, ResourceInputs.Empty, options));

        exception.Message.ShouldContain("Claim-template identities are plan-owned");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Schema: Should reject an unsupported plan schema")]
    public void Compile_OnUnknownSchema_ShouldRejectPlan()
    {
        PlanCase valid = CreateCase("web");
        ResourcePlan invalid = CopyPlan(valid.Plan, schema: "cohesion/plan/v2");

        var exception = Should.Throw<InvalidDataException>(() => Compile(valid with { Plan = invalid }));

        exception.Message.ShouldContain("does not support plan schema");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Render: Should reject an unsupported plan schema without cluster access")]
    public void Render_OnUnknownSchema_ShouldRejectPlan()
    {
        PlanCase valid = CreateCase("web");
        ResourcePlan invalid = CopyPlan(valid.Plan, schema: "cohesion/plan/v2");
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            ((IApplicationResource)new FakeExecutableResource("web")).Id,
            Image);

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => new KubernetesGateway().Render(
                invalid,
                artifact,
                ResourceInputs.Empty,
                [],
                "appa",
                "appa@kubernetes"));

        exception.Message.ShouldContain("does not support plan schema");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build validation: Should reject resource names that overflow compiled object names")]
    public void Validate_OnResourceNameWithOversizedChildObjects_ShouldRejectPlan()
    {
        // Arrange
        ResourcePlan plan = CreatePlan(
            new string('a', 50),
            "Worker",
            WorkloadKind.Deployment,
            1,
            [],
            [],
            [],
            [],
            [],
            []);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => new KubernetesPlanCompiler().Validate(plan));

        // Assert
        exception.Message.ShouldContain("compiled object name");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Schema: Should reject claims that collide with gateway-managed volumes")]
    public void Compile_OnReservedPersistentVolumeName_ShouldRejectPlan()
    {
        ResourcePlan plan = CreatePlan(
            "worker",
            "Worker",
            WorkloadKind.StatefulSet,
            1,
            [],
            [new MountBinding(
                "cohesion-secret",
                "/data",
                ResourceMountKind.Volume,
                "volume:cohesion-secret")],
            [new VolumeSpec("cohesion-secret", ResourceMountKind.Volume, "1Gi", true)],
            [new ServiceSpec("worker-headless", null, null, "tcp", true, true)],
            [],
            []);

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(new PlanCase(plan, CreateManifest(plan))));

        exception.Message.ShouldContain("gateway-managed volume");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Schema: Should reject transport protocols outside plan v1")]
    public void Compile_OnUnsupportedTransportProtocol_ShouldRejectPlan()
    {
        // Arrange
        ResourcePlan plan = CreatePlan(
            "worker",
            "Worker",
            WorkloadKind.Deployment,
            1,
            [new PortBinding("events", 8080, "sctp")],
            [],
            [],
            [new ServiceSpec("worker-events", "events", 8080, "sctp", false, false)],
            [],
            []);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(new PlanCase(plan, CreateManifest(plan))));

        // Assert
        exception.Message.ShouldContain("unsupported protocol 'sctp'");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Schema: Should reject environment names that envFrom would skip")]
    public void Compile_OnInvalidEnvironmentName_ShouldRejectPlan()
    {
        PlanCase valid = CreateCase("web");
        var environment = new Dictionary<string, string>(
            valid.Plan.Container.Environment,
            StringComparer.Ordinal)
        {
            ["1INVALID"] = "value",
        };
        var container = new ContainerSpec(
            valid.Plan.Container.Name,
            valid.Plan.Container.Artifact,
            valid.Plan.Container.Ports,
            valid.Plan.Container.Mounts,
            environment,
            valid.Plan.Container.Probes);
        ResourcePlan plan = CopyPlan(valid.Plan, container: container);

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(valid with { Plan = plan }));

        exception.Message.ShouldContain("[A-Za-z_][A-Za-z0-9_]*");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Schema: Should reject an unknown specification field")]
    public void Parse_OnUnknownSpecificationField_ShouldThrowJsonException()
    {
        ResourcePlan plan = CreateCase("web").Plan;
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        string unknown = json.Insert(json.LastIndexOf('}'), ",\"unexpectedSpec\":true");

        JsonException exception = Should.Throw<JsonException>(
            () => new KubernetesPlanCompiler().Parse(unknown));

        exception.Message.ShouldContain("unexpectedSpec");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Hints: Should warn and continue for an unknown hint")]
    public void Compile_OnUnknownHint_ShouldWarnOnceAndContinue()
    {
        PlanCase valid = CreateCase("web");
        ResourcePlan hinted = CopyPlan(valid.Plan,
            hints: new Dictionary<string, string> { ["example.unsupported"] = "true" });

        KubernetesPlanCompilation result = Compile(valid with { Plan = hinted });

        result.Warnings.ShouldHaveSingleItem();
        result.Warnings[0].ShouldContain("example.unsupported");
        result.Objects.ShouldNotBeEmpty();
    }

    private static KubernetesPlanCompilation Compile(
        PlanCase planCase,
        ResourceInputs? inputs = null,
        KubernetesGatewayOptions? options = null,
        IReadOnlyList<ResourceDependencyObservation>? dependencies = null,
        IReadOnlyList<ResourceEndpoint>? ownEndpoints = null)
    {
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            ((IApplicationResource)new FakeExecutableResource(planCase.Plan.Resource.Value)).Id, Image);
        return new KubernetesPlanCompiler().Compile(
            planCase.Plan, artifact, inputs ?? ResourceInputs.Empty,
            dependencies ?? Array.Empty<ResourceDependencyObservation>(), "appa", "appa@kubernetes",
            options ?? new KubernetesGatewayOptions(),
            ownEndpoints);
    }

    private static PlanCase CreateCase(string shape)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "plans",
            $"{shape}.json");
        ResourcePlan plan = new KubernetesPlanCompiler().Parse(File.ReadAllText(path));
        return new PlanCase(plan, CreateManifest(plan));
    }

    private static ResourcePlan CreatePlan(
        string resource, string kind, WorkloadKind workload, int replicas,
        IReadOnlyList<PortBinding> ports, IReadOnlyList<MountBinding> mounts,
        IReadOnlyList<VolumeSpec> volumes, IReadOnlyList<ServiceSpec> services,
        IReadOnlyList<ExposureSpec> exposures, IReadOnlyList<ProbeMapping> probes) => new(
            ResourcePlan.CurrentSchema, resource, kind,
            new WorkloadSpec(workload, replicas, workload is WorkloadKind.StatefulSet, ReadinessGate.For(workload), 30),
            new ContainerSpec(resource, ArtifactRef.Self, ports, mounts,
                new Dictionary<string, string> { ["COHESION_APPLICATION"] = "appa", ["COHESION_RESOURCE"] = resource, ["COHESION_ENVIRONMENT"] = "Development" }, probes),
            volumes, services, exposures, new Dictionary<string, string>());

    private static ResourcePlan CopyPlan(
        ResourcePlan source,
        string? schema = null,
        IReadOnlyDictionary<string, string>? hints = null,
        ContainerSpec? container = null) =>
        new(schema ?? source.Schema, source.Resource, source.Kind, source.Workload, container ?? source.Container,
            source.Volumes, source.Services, source.Exposures, hints ?? source.Hints);

    private static string[] DependencyVariables(string resource, string endpoint) =>
    [
        ResourceEnvironment.Dependency(resource, endpoint, "URL"),
        ResourceEnvironment.Dependency(resource, endpoint, "HOST"),
        ResourceEnvironment.Dependency(resource, endpoint, "PORT"),
        ResourceEnvironment.Dependency(resource, endpoint, "SCHEME"),
    ];

    private static ResourceManifest CreateManifest(ResourcePlan plan) => new()
    {
        Name = plan.Resource,
        Kind = plan.Kind,
        Application = "appa",
        Endpoints = plan.Container.Ports.Select(endpoint => new ResourceManifestEndpoint
        {
            Name = endpoint.Endpoint,
            Scheme = ResolveScheme(plan, endpoint.Endpoint),
            Protocol = endpoint.Protocol,
            ContainerPort = endpoint.ContainerPort,
        }).ToArray(),
    };

    private static string ResolveScheme(ResourcePlan plan, string endpoint)
    {
        ExposureSpec? exposure = plan.Exposures.FirstOrDefault(candidate => candidate.Endpoint == endpoint);
        if (exposure is not null)
        {
            return exposure.Scheme;
        }

        return endpoint is "http" or "admin" or "control" ? "http" : "tcp";
    }

    private sealed record PlanCase(ResourcePlan Plan, ResourceManifest Manifest);
}
