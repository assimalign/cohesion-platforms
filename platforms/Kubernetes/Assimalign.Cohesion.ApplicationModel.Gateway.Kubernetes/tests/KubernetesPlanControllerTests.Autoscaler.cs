using System.Linq;
using System.Threading.Tasks;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public partial class KubernetesPlanControllerTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Reapply: HPA retains ownership of scale across plan reconciliation")]
    public async Task ReconcileAsync_WithHpaScaleOwnership_ShouldOmitReplicaMutationAsync()
    {
        // Arrange: an autoscaler the cluster runs, not one the plan declares, owns the scale subresource.
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(new KubernetesGatewayOptions(), new KubernetesPlanCompiler(), api, new FakeObservations());
        FakeContext context = CreateContext();
        await controller.ReconcileAsync(context);
        V1Deployment current = api.GetPersisted<V1Deployment>();
        current.Spec.Replicas = 10;
        current.Metadata.ManagedFields = [new V1ManagedFieldsEntry { Manager = "horizontal-pod-autoscaler", Subresource = "scale" }];
        api.ClearRecordings();

        // Act
        await controller.ReconcileAsync(context);

        // Assert
        api.AppliedObjects.OfType<V1Deployment>().Last().Spec.Replicas.ShouldBeNull();
    }
}
