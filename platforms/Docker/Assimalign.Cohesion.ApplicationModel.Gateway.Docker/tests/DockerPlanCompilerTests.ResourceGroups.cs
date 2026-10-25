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

    [Fact(DisplayName = "Cohesion Test [Docker] - Runtime discovery: Binding-only metadata publishes private management access without state declarations")]
    public void Compile_WithBindingOnlyRuntime_ShouldPublishManagementEndpointWithoutInventingReadiness()
    {
        // Arrange: remove ordinary probes and the control-plane fallback, so only runtime metadata can bind http.
        ResourcePlan source = CreateDockerSupportedCase("web");
        ContainerSpec container = new(source.Container.Name, source.Container.Artifact, source.Container.Ports,
            source.Container.Mounts, source.Container.Environment, []);
        ResourcePlan withoutRuntime = new(source.Schema, source.Resource, source.Kind, source.Workload, container,
            source.Volumes, source.Services, source.Exposures, source.Hints);
        using JsonDocument runtime = JsonDocument.Parse("""
            {"schema":"cohesion/resource-runtime/v1","management":{"endpoint":"http","path":"/cohesion/v1","protocol":"cohesion/resource-state/v1"}}
            """);
        ResourcePlan managed = withoutRuntime with { Runtime = runtime.RootElement.Clone() };

        // Act
        DockerPlanCompilation original = Compile(withoutRuntime);
        DockerPlanCompilation result = Compile(managed);

        // Assert
        original.Container.PortBindings.ShouldNotContain(port => port.Endpoint == "http");
        DockerPortPublishPlan management = result.Container.PortBindings.Single(port => port.Endpoint == "http");
        management.HostIp.ShouldBe("127.0.0.1");
        management.HostPort.ShouldBeNull();
        management.ProbeOnly.ShouldBeTrue();
        result.Probes.ShouldBeEmpty();
        result.Network.Name.ShouldBe(original.Network.Name);
    }
}
