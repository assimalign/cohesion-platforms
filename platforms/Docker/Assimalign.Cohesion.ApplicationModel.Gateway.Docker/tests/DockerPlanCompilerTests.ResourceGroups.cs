using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public partial class DockerPlanCompilerTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Logical groups: Moving a resource between groups preserves network, identity and reconciliation hashes")]
    public void Compile_WithDifferentResourceGroups_ShouldPreserveNetworkIdentityAndRenderedObjects()
    {
        // Arrange
        ResourcePlan source = CreateDockerSupportedCase("web");
        ResourcePlan frontend = source with { ResourceGroup = (ResourceGroupName)"frontend" };
        ResourcePlan backend = source with { ResourceGroup = (ResourceGroupName)"backend" };

        // Act
        DockerPlanCompilation original = Compile(source);
        DockerPlanCompilation first = Compile(frontend);
        DockerPlanCompilation second = Compile(backend);

        // Assert: container labels and plan/runtime hashes cannot cause a replacement for a group move.
        first.PlanHash.ShouldBe(original.PlanHash);
        second.PlanHash.ShouldBe(original.PlanHash);
        first.RuntimeHash.ShouldBe(original.RuntimeHash);
        second.RuntimeHash.ShouldBe(original.RuntimeHash);
        first.Network.Name.ShouldBe(original.Network.Name);
        first.Container.Name.ShouldBe(original.Container.Name);
        first.Container.NetworkAliases.ShouldBe(original.Container.NetworkAliases);
        first.Container.Labels.ShouldBe(original.Container.Labels);
        DockerPlanRenderer.Render([first]).ShouldBe(DockerPlanRenderer.Render([original]));
        DockerPlanRenderer.Render([second]).ShouldBe(DockerPlanRenderer.Render([original]));
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Hashes: scale bounds and other platforms' extensions never replace the container")]
    public void Compile_WithScaleBoundsAndForeignExtensions_ShouldPreserveHashes()
    {
        // Arrange: Docker runs one container and reads no extension, so neither declaration changes what it runs.
        ResourcePlan source = CreateDockerSupportedCase("web");
        using JsonDocument targets = JsonDocument.Parse("""{"targetCpuUtilizationPercentage":70}""");
        ResourcePlan declared = source with
        {
            Workload = source.Workload with { Scale = new ScaleBounds(2, 5) },
            Extensions = new System.Collections.Generic.Dictionary<string, JsonElement> { ["kubernetes/hpa"] = targets.RootElement.Clone() },
        };

        // Act
        DockerPlanCompilation original = Compile(source);
        DockerPlanCompilation result = Compile(declared);

        // Assert
        result.PlanHash.ShouldBe(original.PlanHash);
        result.RuntimeHash.ShouldBe(original.RuntimeHash);
        result.Container.Labels.ShouldBe(original.Container.Labels);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Management: the control plane is published on loopback even when probes make it unnecessary for readiness")]
    public void Compile_WithControlPlaneAndProbes_ShouldPublishControlPlaneOnLoopback()
    {
        // Arrange: the plan maps its own probes, so readiness does not need the control plane.
        ResourcePlan source = CreateDockerSupportedCase("web");

        // Act
        DockerPlanCompilation result = Compile(source);

        // Assert: the management API reads configuration status and runs actions through the same private binding.
        DockerPortPublishPlan management = result.Container.PortBindings.Single(port => port.Endpoint == source.ControlPlane.Endpoint);
        management.HostIp.ShouldBe("127.0.0.1");
        management.HostPort.ShouldBeNull();
        management.ProbeOnly.ShouldBeTrue();
    }
}
