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

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Autoscaler: scale bounds compile to an HPA that owns the replica count")]
    public void Compile_WithScaleBounds_ShouldCreateAutoscalerAndLeaveReplicasToIt()
    {
        // Arrange
        PlanCase source = CreateCase("web");
        PlanCase scaled = source with
        {
            Plan = source.Plan with
            {
                Workload = source.Plan.Workload with { Scale = new ScaleBounds(2, 5) },
                Extensions = new System.Collections.Generic.Dictionary<string, JsonElement>
                {
                    ["kubernetes/hpa"] = Parse("""{"targetCpuUtilizationPercentage":70,"scaleDownStabilizationWindowSeconds":120}"""),
                },
            },
        };

        // Act
        KubernetesPlanCompilation original = Compile(source);
        KubernetesPlanCompilation result = Compile(scaled);

        // Assert
        V2HorizontalPodAutoscaler autoscaler = result.Objects.OfType<V2HorizontalPodAutoscaler>().Single();
        autoscaler.Spec.MinReplicas.ShouldBe(2);
        autoscaler.Spec.MaxReplicas.ShouldBe(5);
        autoscaler.Spec.ScaleTargetRef.Kind.ShouldBe("Deployment");
        autoscaler.Spec.ScaleTargetRef.Name.ShouldBe("appa-api");
        autoscaler.Spec.Metrics.ShouldHaveSingleItem().Resource.Target.AverageUtilization.ShouldBe(70);
        autoscaler.Spec.Behavior.ScaleDown.StabilizationWindowSeconds.ShouldBe(120);
        result.Objects.OfType<V1Deployment>().Single().Spec.Replicas.ShouldBeNull();
        result.Readiness.Replicas.ShouldBe(2);
        // The autoscaler is its own object: declaring or changing it never restarts the pods.
        result.PlanHash.ShouldBe(original.PlanHash);
        original.Objects.OfType<V2HorizontalPodAutoscaler>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Autoscaler: without targets the HPA holds 80 percent CPU, and an override wins for the generation")]
    public void Compile_WithBoundsAndOverride_ShouldApplyTheOverrideOverTheDeclaredValue()
    {
        // Arrange
        PlanCase source = CreateCase("web");
        PlanCase scaled = source with { Plan = source.Plan with { Workload = source.Plan.Workload with { Scale = new ScaleBounds(1, 3) } } };
        ResourceInputs inputs = CreateFixtureInputs(scaled.Plan).WithDeploymentRecord(
            "/var/run/cohesion-config",
            "g1",
            new System.Collections.Generic.Dictionary<ResourceExtensionKey, JsonElement> { ["kubernetes/hpa"] = Parse("""{"maxReplicas":8}""") });

        // Act
        KubernetesPlanCompilation declared = Compile(scaled);
        KubernetesPlanCompilation overridden = Compile(scaled, inputs);

        // Assert
        V2HorizontalPodAutoscaler code = declared.Objects.OfType<V2HorizontalPodAutoscaler>().Single();
        code.Spec.MaxReplicas.ShouldBe(3);
        code.Spec.Metrics.ShouldHaveSingleItem().Resource.Name.ShouldBe("cpu");
        code.Spec.Metrics[0].Resource.Target.AverageUtilization.ShouldBe(80);
        overridden.Objects.OfType<V2HorizontalPodAutoscaler>().Single().Spec.MaxReplicas.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Disruption budget: the extension compiles to a PDB over the resource's pods")]
    public void Compile_WithDisruptionBudget_ShouldCreateBudget()
    {
        // Arrange
        PlanCase source = CreateCase("web");
        PlanCase budgeted = source with
        {
            Plan = source.Plan with
            {
                Extensions = new System.Collections.Generic.Dictionary<string, JsonElement>
                {
                    ["kubernetes/pod-disruption-budget"] = Parse("""{"minAvailable":1}"""),
                },
            },
        };

        // Act
        KubernetesPlanCompilation result = Compile(budgeted);

        // Assert
        V1PodDisruptionBudget budget = result.Objects.OfType<V1PodDisruptionBudget>().Single();
        budget.Spec.MinAvailable.Value.ShouldBe("1");
        budget.Spec.MaxUnavailable.ShouldBeNull();
        budget.Spec.Selector.MatchLabels[KubernetesMetadata.ResourceLabel].ShouldBe("appa-api");
        result.PlanHash.ShouldBe(Compile(source).PlanHash);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Disruption budget: a budget stating both counts is refused")]
    public void Compile_WithBothBudgetCounts_ShouldFail()
    {
        // Arrange
        PlanCase source = CreateCase("web");
        PlanCase budgeted = source with
        {
            Plan = source.Plan with
            {
                Extensions = new System.Collections.Generic.Dictionary<string, JsonElement>
                {
                    ["kubernetes/pod-disruption-budget"] = Parse("""{"minAvailable":1,"maxUnavailable":1}"""),
                },
            },
        };

        // Act
        System.InvalidOperationException exception = Should.Throw<System.InvalidOperationException>(() => Compile(budgeted));

        // Assert
        exception.Message.ShouldContain("exactly one", Case.Sensitive);
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
