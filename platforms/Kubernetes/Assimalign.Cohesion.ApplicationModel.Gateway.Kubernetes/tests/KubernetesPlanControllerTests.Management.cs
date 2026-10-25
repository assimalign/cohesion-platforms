using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public partial class KubernetesPlanControllerTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Management: private intent and workload projection commit together with CAS")]
    public async Task ManagementStore_WithConcurrentVersionAndForeignOwner_ShouldPreserveAcceptedIntentAsync()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var store = new KubernetesResourceManagementStore(api, _ => ("appa", "appa@root"));
        byte[] intent = Encoding.UTF8.GetBytes("{\"privateReceipt\":1}");
        byte[] projection = Encoding.UTF8.GetBytes("{\"entries\":[]}");
        (await store.TryWriteProjectionAsync("appa", "worker", null, intent, projection)).ShouldBeTrue();
        ResourceManagementDocument first = (await store.ReadAsync("appa", "worker"))!;
        first.Content.ToArray().ShouldBe(intent);
        V1Secret secret = api.AppliedObjects.OfType<V1Secret>().Last();
        secret.Data[KubernetesResourceManagementStore.ProjectionKey].ShouldBe(projection);
        (await store.TryWriteProjectionAsync("appa", "worker", first.Version, Encoding.UTF8.GetBytes("new"), projection)).ShouldBeTrue();
        (await store.TryWriteProjectionAsync("appa", "worker", first.Version, intent, projection)).ShouldBeFalse();
        Encoding.UTF8.GetString((await store.ReadAsync("appa", "worker"))!.Content.Span).ShouldBe("new");
        var foreign = new KubernetesResourceManagementStore(api, _ => ("appa", "other@root"));
        await Should.ThrowAsync<InvalidDataException>(async () => await foreign.ReadAsync("appa", "worker"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Reapply: HPA retains ownership of scale across plan reconciliation")]
    public async Task ReconcileAsync_WithHpaScaleOwnership_ShouldOmitReplicaMutationAsync()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(new KubernetesGatewayOptions(), new KubernetesPlanCompiler(), api, new FakeObservations());
        FakeContext context = CreateContext();
        await controller.ReconcileAsync(context);
        V1Deployment current = api.GetPersisted<V1Deployment>();
        current.Spec.Replicas = 10;
        current.Metadata.ManagedFields = [new V1ManagedFieldsEntry { Manager = "horizontal-pod-autoscaler", Subresource = "scale" }];
        api.ClearRecordings();
        await controller.ReconcileAsync(context);
        api.AppliedObjects.OfType<V1Deployment>().Last().Spec.Replicas.ShouldBeNull();
    }
}
