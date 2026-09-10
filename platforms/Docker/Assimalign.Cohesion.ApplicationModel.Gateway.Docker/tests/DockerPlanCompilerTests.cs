using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerPlanCompilerTests
{
    private static readonly string Image =
        $"registry.example/test@sha256:{new string('a', 64)}";

    [Theory(DisplayName = "Cohesion Test [Docker] - Compiler: Should map each supported KindMatrix workload to one container")]
    [InlineData("web", WorkloadKind.Deployment)]
    [InlineData("database", WorkloadKind.StatefulSet)]
    [InlineData("generic-volume", WorkloadKind.StatefulSet)]
    [InlineData("daemon-set", WorkloadKind.DaemonSet)]
    [InlineData("job", WorkloadKind.Job)]
    public void Compile_OnSupportedKindMatrixShape_ShouldMapWorkloadToOneContainer(
        string shape,
        WorkloadKind expectedWorkload)
    {
        // Arrange
        ResourcePlan plan = CreateDockerSupportedCase(shape);

        // Act
        DockerPlanCompilation result = Compile(plan);

        // Assert
        result.Container.Workload.ShouldBe(expectedWorkload);
        result.Container.Name.ShouldBe($"appa-{plan.Resource.Value}");
        result.Container.Labels[DockerMetadata.ResourceLabel].ShouldBe(plan.Resource.Value);
        result.Container.Labels[DockerMetadata.PlanHashLabel].ShouldBe(result.PlanHash);
        result.Container.NetworkAliases.ShouldContain(DockerMetadata.Normalize(plan.Resource.Value));
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Build validation: Should reject unsupported replica semantics before gathering")]
    [InlineData("web")]
    [InlineData("generic-volume")]
    public void Validate_OnMultiReplicaKindMatrixShape_ShouldRejectBeforeGathering(string shape)
    {
        // Arrange
        ResourcePlan plan = CreateCase(shape);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => new DockerPlanCompiler().Validate(plan));

        // Assert
        exception.Message.ShouldContain("cannot realize replica count '2'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Schema: Should reject an unsupported plan schema")]
    public void Validate_OnUnknownSchema_ShouldRejectPlan()
    {
        // Arrange
        ResourcePlan valid = CreateDockerSupportedCase("web");
        ResourcePlan invalid = CopyPlan(valid, schema: "cohesion/plan/v2");

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => new DockerPlanCompiler().Validate(invalid));

        // Assert
        exception.Message.ShouldContain("does not support plan schema", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Schema: Should reject an unknown specification field")]
    public void Parse_OnUnknownSpecificationField_ShouldThrowJsonException()
    {
        // Arrange
        ResourcePlan plan = CreateCase("web");
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        string unknown = json.Insert(json.LastIndexOf('}'), ",\"unexpectedSpec\":true");

        // Act
        JsonException exception = Should.Throw<JsonException>(
            () => new DockerPlanCompiler().Parse(unknown));

        // Assert
        exception.Message.ShouldContain("unexpectedSpec", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Hints: Should warn once and continue for an unknown hint")]
    public void Compile_OnUnknownHint_ShouldWarnOnceAndContinue()
    {
        // Arrange
        ResourcePlan valid = CreateDockerSupportedCase("web");
        ResourcePlan hinted = CopyPlan(
            valid,
            hints: new Dictionary<string, string>
            {
                ["example.unsupported"] = "true",
            });

        // Act
        DockerPlanCompilation result = Compile(hinted);

        // Assert
        result.Warnings.ShouldHaveSingleItem();
        result.Warnings[0].ShouldContain("example.unsupported", Case.Sensitive);
        result.Container.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Volumes: Should create deterministic named claim volumes")]
    public void Compile_OnStatefulVolumePlan_ShouldCreateNamedClaimVolume()
    {
        // Arrange
        ResourcePlan plan = CreateCase("database");

        // Act
        DockerPlanCompilation result = Compile(plan);

        // Assert
        DockerVolumePlan volume = result.Volumes.ShouldHaveSingleItem();
        volume.Name.ShouldBe("appa-appa-database-data");
        volume.Claim.ShouldBe("data");
        volume.Target.ShouldBe("/var/lib/database");
        volume.Labels[DockerMetadata.PlanHashLabel].ShouldBe(result.PlanHash);
        DockerVolumeMountPlan mount = result.Container.VolumeMounts.ShouldHaveSingleItem();
        mount.Source.ShouldBe(volume.Name);
        mount.Target.ShouldBe(volume.Target);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Inputs: Should isolate secrets and bootstrap credentials on tmpfs")]
    public void Compile_OnSensitiveFileInputs_ShouldUseTmpfsWithoutEnvironmentLeakage()
    {
        // Arrange
        ResourcePlan plan = CreatePlan(
            "configured-worker",
            WorkloadKind.Deployment,
            [],
            [
                new MountBinding(
                    "settings",
                    "/app/settings.json",
                    ResourceMountKind.Configuration,
                    "parameter:settings"),
                new MountBinding(
                    "password",
                    "/run/secrets/password",
                    ResourceMountKind.Secret,
                    "secret:password"),
            ],
            [],
            [],
            [],
            []);
        var inputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>
            {
                ["settings"] = ResourceMountInput.Resolved(
                    "parameter:settings",
                    Encoding.UTF8.GetBytes("configuration")),
                ["password"] = ResourceMountInput.Resolved(
                    "secret:password",
                    Encoding.UTF8.GetBytes("secret-value")),
            },
            Encoding.UTF8.GetBytes("bootstrap-value"));

        // Act
        DockerPlanCompilation result = Compile(plan, inputs);

        // Assert
        DockerFilePlan configuration = result.Files.Single(
            file => file.Path == "/app/settings.json");
        DockerFilePlan secret = result.Files.Single(
            file => file.Path == "/run/secrets/password");
        DockerFilePlan bootstrap = result.Files.Single(
            file => file.Path == "/var/run/cohesion/bootstrap.token");
        configuration.Sensitive.ShouldBeFalse();
        secret.Sensitive.ShouldBeTrue();
        bootstrap.Sensitive.ShouldBeTrue();
        result.Container.Tmpfs.ShouldBe(["/run/secrets", "/var/run/cohesion"]);
        result.Container.Environment[ResourceEnvironment.BootstrapTokenPath]
            .ShouldBe("/var/run/cohesion/bootstrap.token");
        result.Container.Environment.Values.ShouldNotContain("secret-value");
        result.Container.Environment.Values.ShouldNotContain("bootstrap-value");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Ports: Should publish public ports and bind probes to loopback")]
    public void Compile_OnPublicAndProbedEndpoints_ShouldCreateScopedPortBindings()
    {
        // Arrange
        ResourcePlan plan = CreateDockerSupportedCase("web");

        // Act
        DockerPlanCompilation result = Compile(plan);

        // Assert
        DockerPortPublishPlan published = result.Container.PortBindings.Single(
            binding => !binding.ProbeOnly);
        published.Endpoint.ShouldBe("https");
        published.HostIp.ShouldBe("0.0.0.0");
        published.HostPort.ShouldBe(8443);
        published.ContainerPort.ShouldBe(8443);
        result.Container.Environment[ResourceEnvironment.Endpoint("https", "PUBLIC_URL")]
            .ShouldBe("https://localhost:8443");
        DockerPortPublishPlan[] probeBindings = result.Container.PortBindings
            .Where(binding => binding.ProbeOnly)
            .ToArray();
        probeBindings.Length.ShouldBe(2);
        probeBindings.ShouldAllBe(binding =>
            binding.HostIp == "127.0.0.1" && binding.HostPort == null);
        probeBindings.Select(binding => binding.Endpoint)
            .ShouldBe(["https", "http"]);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Compiler: Should map DaemonSet to one single-engine container")]
    public void Compile_OnDaemonSetPlan_ShouldCreateOneSingleEngineContainer()
    {
        // Arrange
        ResourcePlan plan = CreateCase("daemon-set");

        // Act
        DockerPlanCompilation result = Compile(plan);

        // Assert
        result.Container.Workload.ShouldBe(WorkloadKind.DaemonSet);
        result.Container.Name.ShouldBe("appa-node-agent");
        result.Container.NetworkAliases.ShouldContain("node-agent");
        result.Container.NetworkAliases.ShouldContain("node-agent-control");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Render: Should mark Job containers as run once")]
    public void Render_OnJobPlan_ShouldMarkContainerAsRunOnce()
    {
        // Arrange
        DockerPlanCompilation compilation = Compile(CreateCase("job"));

        // Act
        string result = DockerPlanRenderer.Render([compilation]);

        // Assert
        result.ShouldContain("workload: \"Job\"", Case.Sensitive);
        result.ShouldContain("run_once: true", Case.Sensitive);
        result.ShouldNotContain("run_once: false", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Compiler: Should reject unsupported gRPC probes")]
    public void Compile_OnGrpcProbePlan_ShouldRejectUnsupportedSpecification()
    {
        // Arrange
        ResourcePlan valid = CreateCase("job");
        var container = new ContainerSpec(
            valid.Container.Name,
            valid.Container.Artifact,
            valid.Container.Ports,
            valid.Container.Mounts,
            valid.Container.Environment,
            [new ProbeMapping("readiness", "control", ProbeKind.Grpc, "health", [])]);
        ResourcePlan invalid = CopyPlan(valid, container: container);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => Compile(invalid));

        // Assert
        exception.Message.ShouldContain("does not support gRPC health probes", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Plan hash: Should ignore bootstrap credential rotation")]
    public void Compile_OnRotatedBootstrapCredential_ShouldKeepPlanHashStable()
    {
        // Arrange
        ResourcePlan plan = CreateCase("job");
        var firstInputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>(),
            Encoding.UTF8.GetBytes("first"));
        var secondInputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>(),
            Encoding.UTF8.GetBytes("second"));

        // Act
        DockerPlanCompilation first = Compile(plan, firstInputs);
        DockerPlanCompilation second = Compile(plan, secondInputs);

        // Assert
        first.PlanHash.ShouldBe(second.PlanHash);
        first.RuntimeHash.ShouldNotBe(second.RuntimeHash);
        first.Files.ShouldHaveSingleItem().Content.ToArray()
            .ShouldBe(Encoding.UTF8.GetBytes("first"));
        second.Files.ShouldHaveSingleItem().Content.ToArray()
            .ShouldBe(Encoding.UTF8.GetBytes("second"));
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Render: Should match deterministic KindMatrix Compose output")]
    public void Render_OnSupportedKindMatrixShapes_ShouldMatchDeterministicGoldenOutput()
    {
        // Arrange
        string[] shapes = ["web", "database", "generic-volume", "daemon-set", "job"];
        DockerPlanCompilation[] firstCompilations = shapes
            .Select(CreateDockerSupportedCase)
            .Select(plan => Compile(plan))
            .ToArray();
        DockerPlanCompilation[] secondCompilations = shapes
            .Select(CreateDockerSupportedCase)
            .Select(plan => Compile(plan))
            .ToArray();
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "render",
            "kind-matrix.yaml");

        // Act
        string first = DockerPlanRenderer.Render(firstCompilations);
        string second = DockerPlanRenderer.Render(secondCompilations);

        // Assert
        first.ShouldBe(second);
        first.ShouldBe(
            File.ReadAllText(fixture).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static DockerPlanCompilation Compile(
        ResourcePlan plan,
        ResourceInputs? inputs = null)
    {
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            ((IApplicationResource)new FakeExecutableResource(plan.Resource.Value)).Id,
            Image);
        return new DockerPlanCompiler().Compile(
            plan,
            artifact,
            inputs ?? ResourceInputs.Empty,
            [],
            "appa",
            "appa@docker");
    }

    private static ResourcePlan CreateCase(string shape)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "plans",
            $"{shape}.json");
        return new DockerPlanCompiler().Parse(File.ReadAllText(path));
    }

    private static ResourcePlan CreateDockerSupportedCase(string shape)
    {
        ResourcePlan plan = CreateCase(shape);
        if (plan.Workload.Replicas == 1)
        {
            return plan;
        }

        var workload = new WorkloadSpec(
            plan.Workload.Kind,
            1,
            plan.Workload.StableIdentity,
            plan.Workload.Gate,
            plan.Workload.StopGraceSeconds);
        return CopyPlan(plan, workload: workload);
    }

    private static ResourcePlan CreatePlan(
        string resource,
        WorkloadKind workload,
        IReadOnlyList<PortBinding> ports,
        IReadOnlyList<MountBinding> mounts,
        IReadOnlyList<VolumeSpec> volumes,
        IReadOnlyList<ServiceSpec> services,
        IReadOnlyList<ExposureSpec> exposures,
        IReadOnlyList<ProbeMapping> probes) => new(
            ResourcePlan.CurrentSchema,
            resource,
            "Worker",
            new WorkloadSpec(
                workload,
                1,
                workload is WorkloadKind.StatefulSet,
                ReadinessGate.For(workload),
                30),
            new ContainerSpec(
                resource,
                ArtifactRef.Self,
                ports,
                mounts,
                new Dictionary<string, string>
                {
                    ["COHESION_APPLICATION"] = "appa",
                    ["COHESION_RESOURCE"] = resource,
                    ["COHESION_ENVIRONMENT"] = "Development",
                },
                probes),
            volumes,
            services,
            exposures,
            new Dictionary<string, string>());

    private static ResourcePlan CopyPlan(
        ResourcePlan source,
        string? schema = null,
        WorkloadSpec? workload = null,
        ContainerSpec? container = null,
        IReadOnlyDictionary<string, string>? hints = null) =>
        new(
            schema ?? source.Schema,
            source.Resource,
            source.Kind,
            workload ?? source.Workload,
            container ?? source.Container,
            source.Volumes,
            source.Services,
            source.Exposures,
            hints ?? source.Hints);
}
