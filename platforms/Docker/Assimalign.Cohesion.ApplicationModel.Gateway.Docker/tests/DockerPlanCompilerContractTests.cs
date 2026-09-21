using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public partial class DockerPlanCompilerTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Certificate: Should retain the complete bundle in one sensitive tmpfs file")]
    public void Compile_OnCertificateMount_ShouldPreserveBundleAndMountPath()
    {
        // Arrange / Act
        ResourcePlan plan = CreateDockerSupportedCase("web");
        DockerPlanCompilation result = Compile(plan);

        // Assert
        DockerFilePlan file = result.Files.ShouldHaveSingleItem();
        file.Path.ShouldBe("/cohesion/mounts/tls");
        file.Sensitive.ShouldBeTrue();
        file.Content.ToArray().ShouldBe(CreateFixtureInputs(plan).Mounts["tls"].Content.ToArray());
        result.Container.Tmpfs.ShouldHaveSingleItem().ShouldBe("/cohesion/mounts");
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Certificate: Should reject explicit null in direct and JSON plans")]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_OnNullCertificate_ShouldNameEndpoint(bool deserialize)
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        PortBinding[] ports = source.Container.Ports.Select(port => port with { Certificate = null! }).ToArray();
        ResourcePlan plan = CopyPlan(source, container: new ContainerSpec(
            source.Container.Name, source.Container.Artifact, ports, source.Container.Mounts,
            source.Container.Environment, source.Container.Probes));
        if (deserialize)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
            plan = new DockerPlanCompiler().Parse(json);
        }

        // Act / Assert
        Should.Throw<InvalidDataException>(() => new DockerPlanCompiler().Validate(plan)).Message
            .ShouldContain("endpoint 'http' certificate mount must not be null", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Certificate: Should reject missing or non-Secret certificate mounts")]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_OnInvalidCertificateMount_ShouldNameEndpointAndMount(bool configurationMount)
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        MountBinding[] mounts = configurationMount
            ? [new MountBinding("tls", "/cohesion/mounts/tls", ResourceMountKind.Configuration, null)]
            : [];
        ResourcePlan invalid = CopyPlan(source, container: new ContainerSpec(
            source.Container.Name, source.Container.Artifact, source.Container.Ports, mounts,
            source.Container.Environment, source.Container.Probes));

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(() => Compile(invalid));

        // Assert
        exception.Message.ShouldContain("https", Case.Sensitive);
        exception.Message.ShouldContain("tls", Case.Sensitive);
        exception.Message.ShouldContain("Secret", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Certificate: Should accept an empty certificate and case-insensitive public sentinel")]
    [InlineData("")]
    [InlineData("public")]
    [InlineData("PuBlIc")]
    public void Compile_OnCertificateSentinel_ShouldRequireNoMount(string certificate)
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        PortBinding[] ports = source.Container.Ports.Select(port => new PortBinding(
            port.Endpoint, port.ContainerPort, port.Protocol, port.Scheme, certificate)).ToArray();
        ResourcePlan plan = CopyPlan(source, container: new ContainerSpec(
            source.Container.Name, source.Container.Artifact, ports, [],
            source.Container.Environment, source.Container.Probes));

        // Act / Assert
        Compile(plan).Files.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Inputs: Should stage trust and telemetry credentials together on tmpfs")]
    public void Compile_OnTrustAndTelemetryInputs_ShouldProtectFilesAndPreservePlanHash()
    {
        // Arrange
        ResourcePlan plan = CreateCase("job");
        byte[] trust = Encoding.UTF8.GetBytes("test-trust-pem");
        byte[] headers = Encoding.UTF8.GetBytes("Authorization=Bearer private-telemetry-token");
        byte[] bootstrap = Encoding.UTF8.GetBytes("test-bootstrap-token");
        var inputs = new ResourceInputs(new Dictionary<string, ResourceMountInput>(), bootstrap, default, trust);
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            ((IApplicationResource)new FakeExecutableResource(plan.Resource.Value)).Id, Image);

        // Act
        DockerPlanCompilation populated = new DockerPlanCompiler().Compile(
            plan, artifact, inputs, [], "appa", "appa@docker", telemetryHeaders: headers);
        DockerPlanCompilation empty = Compile(plan, ResourceInputs.Empty);

        // Assert
        DockerFilePlan trustFile = populated.Files.Single(file => file.Path == "/var/run/cohesion/trust.pem");
        DockerFilePlan headersFile = populated.Files.Single(file => file.Path == "/var/run/cohesion/telemetry.headers");
        trustFile.Sensitive.ShouldBeTrue();
        headersFile.Sensitive.ShouldBeTrue();
        trustFile.Content.ToArray().ShouldBe(trust);
        headersFile.Content.ToArray().ShouldBe(headers);
        populated.Files.Count.ShouldBe(3);
        populated.Files.Single(file => file.Path == "/var/run/cohesion/bootstrap.token").Content.ToArray().ShouldBe(bootstrap);
        populated.Container.Tmpfs.ShouldHaveSingleItem().ShouldBe("/var/run/cohesion");
        populated.Container.Environment[ResourceEnvironment.TrustBundlePath].ShouldBe(trustFile.Path);
        populated.Container.Environment[ResourceEnvironment.TelemetryHeadersPath].ShouldBe(headersFile.Path);
        populated.Container.Environment.Values.ShouldNotContain(Encoding.UTF8.GetString(headers));
        populated.Container.Environment.Values.ShouldNotContain(Encoding.UTF8.GetString(trust));
        populated.Container.Environment.ShouldNotContainKey("COHESION_TELEMETRY_ENDPOINT");
        populated.Container.Environment.ShouldNotContainKey("COHESION_TELEMETRY_PROTOCOL");
        populated.PlanHash.ShouldBe(empty.PlanHash);
        populated.RuntimeHash.ShouldNotBe(empty.RuntimeHash);
        empty.Files.ShouldBeEmpty();
        empty.Container.Tmpfs.ShouldBeEmpty();
        empty.Container.Environment.ShouldNotContainKey(ResourceEnvironment.TrustBundlePath);
        empty.Container.Environment.ShouldNotContainKey(ResourceEnvironment.TelemetryHeadersPath);
        DockerPlanRenderer.Render([populated]).ShouldNotContain("private-telemetry-token", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Inputs: An isolated trust or telemetry file creates only its one tmpfs carrier")]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_OnSingleProtectedInput_ShouldCreateOnlyItsFileAndVariable(bool telemetry)
    {
        // Arrange
        ResourcePlan plan = CreateCase("job");
        byte[] content = Encoding.UTF8.GetBytes("test-protected-document");
        var inputs = new ResourceInputs(new Dictionary<string, ResourceMountInput>(), default, default,
            telemetry ? default : content);
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            ((IApplicationResource)new FakeExecutableResource(plan.Resource.Value)).Id, Image);

        // Act
        DockerPlanCompilation result = new DockerPlanCompiler().Compile(
            plan, artifact, inputs, [], "appa", "appa@docker", telemetryHeaders: telemetry ? content : default);

        // Assert
        DockerFilePlan file = result.Files.ShouldHaveSingleItem();
        file.Path.ShouldBe(telemetry ? "/var/run/cohesion/telemetry.headers" : "/var/run/cohesion/trust.pem");
        file.Content.ToArray().ShouldBe(content);
        file.Sensitive.ShouldBeTrue();
        result.Container.Tmpfs.ShouldHaveSingleItem().ShouldBe("/var/run/cohesion");
        result.Container.Environment[telemetry ? ResourceEnvironment.TelemetryHeadersPath : ResourceEnvironment.TrustBundlePath].ShouldBe(file.Path);
        result.Container.Environment.ShouldNotContainKey(telemetry ? ResourceEnvironment.TrustBundlePath : ResourceEnvironment.TelemetryHeadersPath);
        result.Container.Environment.ShouldNotContainKey(ResourceEnvironment.BootstrapTokenPath);
        result.Container.Environment.ShouldNotContainKey(ResourceEnvironment.TelemetryEndpoint);
        result.Container.Environment.ShouldNotContainKey(ResourceEnvironment.TelemetryProtocol);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Inputs: Should reject missing Secret bytes in live compilation")]
    public void Compile_OnMissingSecretInput_ShouldFailLiveMaterialization()
    {
        // Arrange
        ResourcePlan plan = CreateDockerSupportedCase("web");

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => Compile(plan, ResourceInputs.Empty))
            .Message.ShouldContain("tls", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Control plane: Should synthesize readiness from the private endpoint and path")]
    public void Compile_OnMissingReadiness_ShouldUseControlPlaneSchemePortAndPath()
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        var ports = new[]
        {
            new PortBinding("http", 8080, "tcp", "https", ""),
            source.Container.Ports[1],
        };
        ResourcePlan plan = CopyPlan(source, container: new ContainerSpec(
            source.Container.Name, source.Container.Artifact, ports, source.Container.Mounts,
            source.Container.Environment, []));

        // Act
        DockerPlanCompilation result = Compile(plan);

        // Assert
        DockerProbePlan readiness = result.Probes.ShouldHaveSingleItem();
        readiness.Role.ShouldBe("readiness");
        readiness.Kind.ShouldBe(ProbeKind.Http);
        readiness.Endpoint.ShouldBe("http");
        readiness.Scheme.ShouldBe("https");
        readiness.ContainerPort.ShouldBe(8080);
        readiness.Value.ShouldBe("/cohesion/v1");
        result.Container.Environment[ResourceEnvironment.Endpoint("http", "SCHEME")].ShouldBe("https");
        result.Container.PortBindings.ShouldContain(port => port.Endpoint == "http" && port.ProbeOnly);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Control plane: Should preserve an explicitly disabled readiness probe")]
    public void Compile_OnExplicitReadinessNone_ShouldNotSynthesizeReadiness()
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        ResourcePlan plan = CopyPlan(source, container: new ContainerSpec(
            source.Container.Name, source.Container.Artifact, source.Container.Ports, source.Container.Mounts,
            source.Container.Environment, [new ProbeMapping("readiness", null, ProbeKind.None, null, [])]));

        // Act / Assert
        Compile(plan).Probes.ShouldHaveSingleItem().Kind.ShouldBe(ProbeKind.None);
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Restart: Should use plan policy before the compatibility manifest fallback")]
    [InlineData("Never", RestartPolicy.Never)]
    [InlineData("Always", RestartPolicy.Always)]
    [InlineData("OnFailure", RestartPolicy.OnFailure)]
    [InlineData("", RestartPolicy.Always)]
    public void ReadRestartPolicy_OnPlanPolicy_ShouldSelectAuthoritativeValue(string policy, RestartPolicy expected)
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        ResourcePlan plan = CopyPlan(source, workload: new WorkloadSpec(
            source.Workload.Kind, 1, false, source.Workload.Gate, 30, policy));
        var resource = new DockerTestResource("worker", RestartPolicy.Always, WorkloadKind.Deployment);

        // Act / Assert
        DockerPlanController.ReadRestartPolicy(plan, resource).ShouldBe(expected);
        DockerPlanRenderer.Render([Compile(plan)]).ShouldContain("restart_owner: \"gateway\"", Case.Sensitive);
    }
}
