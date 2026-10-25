using System.Linq;
using System.Text.Json;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public partial class KubernetesPlanCompilerTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Logical groups: Moving a resource between groups preserves deployment identity and rollout hash")]
    public void Compile_WithDifferentResourceGroups_ShouldPreserveNamespaceSelectorsAndRenderedObjects()
    {
        // Arrange
        PlanCase source = CreateCase("web");
        PlanCase frontend = source with { Plan = source.Plan with { ResourceGroup = (ResourceGroupName)"frontend" } };
        PlanCase backend = source with { Plan = source.Plan with { ResourceGroup = (ResourceGroupName)"backend" } };

        // Act
        KubernetesPlanCompilation original = Compile(source);
        KubernetesPlanCompilation first = Compile(frontend);
        KubernetesPlanCompilation second = Compile(backend);

        // Assert: group membership remains management metadata and cannot trigger a rollout.
        first.PlanHash.ShouldBe(original.PlanHash);
        second.PlanHash.ShouldBe(original.PlanHash);
        V1Deployment workload = first.Objects.OfType<V1Deployment>().Single();
        V1Deployment expected = original.Objects.OfType<V1Deployment>().Single();
        workload.Metadata.NamespaceProperty.ShouldBe("appa");
        workload.Metadata.Name.ShouldBe(expected.Metadata.Name);
        workload.Spec.Selector.MatchLabels.ShouldBe(expected.Spec.Selector.MatchLabels);
        KubernetesPlanRenderer.Render(first).ShouldBe(KubernetesPlanRenderer.Render(original));
        KubernetesPlanRenderer.Render(second).ShouldBe(KubernetesPlanRenderer.Render(original));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Runtime discovery: Binding-only metadata installs management projection without producer state declarations")]
    public void Compile_WithBindingOnlyRuntime_ShouldInstallProtectedProjectionAndInstanceBinding()
    {
        // Arrange: capabilities and effective baselines are discovered only when the resource starts.
        PlanCase source = CreateCase("web");
        using JsonDocument runtime = JsonDocument.Parse("""
            {"schema":"cohesion/resource-runtime/v1","management":{"endpoint":"http","path":"/cohesion/v1","protocol":"cohesion/resource-state/v1"}}
            """);
        PlanCase managed = source with { Plan = source.Plan with { Runtime = runtime.RootElement.Clone() } };

        // Act
        KubernetesPlanCompilation result = Compile(managed);

        // Assert
        V1PodSpec pod = result.Objects.OfType<V1Deployment>().Single().Spec.Template.Spec;
        V1SecretVolumeSource projection = pod.Volumes.Single(volume => volume.Name == "cohesion-state").Secret;
        projection.Items.ShouldHaveSingleItem().Key.ShouldBe(KubernetesResourceManagementStore.ProjectionKey);
        projection.DefaultMode.ShouldBe(288);
        pod.Containers[0].VolumeMounts.Single(mount => mount.Name == "cohesion-state").ReadOnlyProperty.ShouldBe(true);
        pod.Containers[0].Env.Single(variable => variable.Name == AppEnvironment.Variables.ResourceInstance)
            .ValueFrom.FieldRef.FieldPath.ShouldBe("metadata.uid");
        pod.Containers[0].Env.Single(variable => variable.Name == AppEnvironment.Variables.ResourceStatePath)
            .Value.ShouldBe("/var/run/cohesion-state/runtime-state.json");
        result.Objects.OfType<V1Secret>().Single(secret => secret.Metadata.Name.EndsWith("-management", System.StringComparison.Ordinal))
            .Data.ShouldBeNull();
    }
}
