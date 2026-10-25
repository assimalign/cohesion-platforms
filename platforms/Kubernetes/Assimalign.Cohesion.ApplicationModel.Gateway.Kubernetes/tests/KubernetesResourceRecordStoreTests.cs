using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesResourceRecordStoreTests
{
    private const string Owner = "appa@root";

    private static readonly IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> _noClaims =
        new Dictionary<ApplicationName, ReadOnlyMemory<byte>>();

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a record round-trips through the ConfigMap its pod template reads and the claims ConfigMap")]
    public async Task TryWriteAsync_ThenRead_ShouldRoundTripTheRecord()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" });
        ResourceRecord record = ResourceRecord.Create(
            "g1",
            Json("""{"Web:Cors":{"origin":"a"}}"""),
            new Dictionary<ApplicationName, ReadOnlyMemory<byte>> { ["appb"] = Json("""{"Orders:Schema":{"invoices":{}}}""") },
            Json("""{"Web:Cors":{"origin":"b"}}"""),
            new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> { ["kubernetes/hpa"] = Json("""{"maxReplicas":8}""") });

        // Act
        bool written = await store.TryWriteAsync("appa", "web", record, null);
        ResourceRecord? read = await store.ReadAsync("appa", "web");

        // Assert
        written.ShouldBeTrue();
        read.ShouldNotBeNull();
        read.Generation.ShouldBe("g1");
        read.Version.ShouldBe(record.Version);
        read.Digest.ShouldBe(record.Digest);
        V1ConfigMap layers = api.Get("web-record-k1");
        layers.Data.Keys.ShouldBe(["deployment.json", "override.json", "platform.kubernetes.hpa.json"], ignoreOrder: true);
        layers.Metadata.Labels[KubernetesResourceRecordStore.RecordLabel].ShouldBe("web");
        layers.Metadata.Labels.ContainsKey(KubernetesMetadata.ResourceLabel).ShouldBeFalse();
        V1ConfigMap claims = api.Get("web-claims");
        claims.Data.Keys.ShouldBe(["claims.appb.json"]);
        claims.Metadata.Annotations[KubernetesResourceRecordStore.GenerationAnnotation].ShouldBe("g1");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.RecordKeyAnnotation].ShouldBe("k1");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: without a validated plan, a generation's record is written under the generation")]
    public async Task TryWriteAsync_WithoutRecordKey_ShouldNameTheRecordForTheGeneration()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string>());

        // Act
        bool written = await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g1", Json("""{"A":{}}"""), _noClaims, default), null);

        // Assert
        written.ShouldBeTrue();
        api.Names.ShouldBe(["appa/web-record-g1", "appa/web-claims"], ignoreOrder: true);
        (await store.ReadAsync("appa", "web"))!.Generation.ShouldBe("g1");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a removed layer is removed from the ConfigMap")]
    public async Task TryWriteAsync_WithoutOverride_ShouldRemoveTheOverrideKey()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" });
        ResourceRecord overridden = ResourceRecord.Create("g1", default, _noClaims, Json("""{"Web:Cors":{}}"""));
        (await store.TryWriteAsync("appa", "web", overridden, null)).ShouldBeTrue();

        // Act
        ResourceRecord reset = ResourceRecord.Create("g1", default, _noClaims, default);
        bool written = await store.TryWriteAsync("appa", "web", reset, overridden.Version);

        // Assert
        written.ShouldBeTrue();
        (api.Get("web-record-k1").Data ?? new Dictionary<string, string>()).ContainsKey("override.json").ShouldBeFalse();
        (await store.ReadAsync("appa", "web"))!.Version.ShouldBe(reset.Version);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a write naming a version that moved is refused")]
    public async Task TryWriteAsync_WithStaleVersion_ShouldRefuse()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" });
        ResourceRecord first = ResourceRecord.Create("g1", default, _noClaims, default);
        ResourceRecord second = ResourceRecord.Create("g1", default, _noClaims, Json("""{"A":{"b":1}}"""));
        (await store.TryWriteAsync("appa", "web", first, null)).ShouldBeTrue();
        (await store.TryWriteAsync("appa", "web", second, first.Version)).ShouldBeTrue();

        // Act
        bool stale = await store.TryWriteAsync("appa", "web", first, first.Version);

        // Assert
        stale.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a ConfigMap changed by another writer after the read refuses the write")]
    public async Task TryWriteAsync_WhenTheObjectMovedUnderneath_ShouldRefuse()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" });
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g1", default, _noClaims, default), null)).ShouldBeTrue();
        api.BeforeApply = name =>
        {
            if (name == "web-record-k1")
            {
                api.Touch(name);
            }
        };

        // Act
        bool written = await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g1", default, _noClaims, Json("""{"A":{}}""")), null);

        // Assert
        written.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a new generation the pods read the same way rewrites their ConfigMap in place")]
    public async Task TryWriteAsync_NewGenerationWithSameKey_ShouldRewriteTheCurrentConfigMap()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        KubernetesResourceRecordStore store = CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" });
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g1", default, _noClaims, Json("""{"A":{}}""")), null)).ShouldBeTrue();

        // Act: the new generation discarded the override, and its pod template still reads k1.
        bool written = await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g2", default, _noClaims, default), null);

        // Assert
        written.ShouldBeTrue();
        api.Names.ShouldBe(["appa/web-record-k1", "appa/web-claims"], ignoreOrder: true);
        (api.Get("web-record-k1").Data ?? new Dictionary<string, string>()).ContainsKey("override.json").ShouldBeFalse();
        V1ConfigMap claims = api.Get("web-claims");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.GenerationAnnotation].ShouldBe("g2");
        claims.Metadata.Annotations.ContainsKey(KubernetesResourceRecordStore.PreviousRecordKeyAnnotation).ShouldBeFalse();
        (await store.ReadAsync("appa", "web"))!.Generation.ShouldBe("g2");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a new pod template keeps the previous record for the rollout and retires the one before")]
    public async Task TryWriteAsync_AcrossRecordKeys_ShouldKeepCurrentAndPrevious()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        var keys = new Dictionary<string, string>();
        KubernetesResourceRecordStore store = CreateStore(api, keys);
        foreach ((string generation, string key) in new[] { ("g1", "k1"), ("g2", "k2") })
        {
            keys["web"] = key;
            (await store.TryWriteAsync("appa", "web", ResourceRecord.Create(generation, Json("""{"A":{}}"""), _noClaims, default), null)).ShouldBeTrue();
        }

        // Act
        keys["web"] = "k3";
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g3", Json("""{"A":{}}"""), _noClaims, default), null)).ShouldBeTrue();

        // Assert
        api.Names.ShouldBe(["appa/web-record-k2", "appa/web-record-k3", "appa/web-claims"], ignoreOrder: true);
        V1ConfigMap claims = api.Get("web-claims");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.GenerationAnnotation].ShouldBe("g3");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.RecordKeyAnnotation].ShouldBe("k3");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.PreviousRecordKeyAnnotation].ShouldBe("k2");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: returning to the previous pod template reuses its record and keeps the one it left")]
    public async Task TryWriteAsync_BackToPreviousKey_ShouldKeepBothRecords()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        var keys = new Dictionary<string, string>();
        KubernetesResourceRecordStore store = CreateStore(api, keys);
        foreach ((string generation, string key) in new[] { ("g1", "k1"), ("g2", "k2") })
        {
            keys["web"] = key;
            (await store.TryWriteAsync("appa", "web", ResourceRecord.Create(generation, Json("""{"A":{}}"""), _noClaims, default), null)).ShouldBeTrue();
        }

        // Act
        keys["web"] = "k1";
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g3", Json("""{"A":{}}"""), _noClaims, default), null)).ShouldBeTrue();

        // Assert
        api.Names.ShouldBe(["appa/web-record-k1", "appa/web-record-k2", "appa/web-claims"], ignoreOrder: true);
        V1ConfigMap claims = api.Get("web-claims");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.RecordKeyAnnotation].ShouldBe("k1");
        claims.Metadata.Annotations[KubernetesResourceRecordStore.PreviousRecordKeyAnnotation].ShouldBe("k2");
        (await store.ReadAsync("appa", "web"))!.Generation.ShouldBe("g3");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: deleting a record removes every record ConfigMap it names and the claims")]
    public async Task DeleteAsync_ShouldRemoveTheRecordObjects()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        var keys = new Dictionary<string, string> { ["web"] = "k1" };
        KubernetesResourceRecordStore store = CreateStore(api, keys);
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g1", default, _noClaims, default), null)).ShouldBeTrue();
        keys["web"] = "k2";
        (await store.TryWriteAsync("appa", "web", ResourceRecord.Create("g2", default, _noClaims, default), null)).ShouldBeTrue();

        // Act
        await store.DeleteAsync("appa", "web");

        // Assert
        api.Names.ShouldBeEmpty();
        (await store.ReadAsync("appa", "web")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a record another gateway owner wrote is refused, never adopted")]
    public async Task ReadAsync_WithForeignOwner_ShouldThrow()
    {
        // Arrange
        var api = new FakeConfigMapApi();
        (await CreateStore(api, new Dictionary<string, string> { ["web"] = "k1" })
            .TryWriteAsync("appa", "web", ResourceRecord.Create("g1", default, _noClaims, default), null)).ShouldBeTrue();
        var foreign = new KubernetesResourceRecordStore(api, _ => ("appa", "other@root"), static (_, _) => null);

        // Act
        Func<Task> read = async () => await foreign.ReadAsync("appa", "web");

        // Assert
        await read.ShouldThrowAsync<InvalidDataException>();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Records: a record key is the start of the plan's pod-template hash")]
    public void RecordKey_ShouldTakeSixteenCharacters()
    {
        // Act
        string key = KubernetesResourceRecordStore.RecordKey(new string('c', 64));

        // Assert
        key.ShouldBe(new string('c', 16));
    }

    // The keys stand in for the gateway's validated plans: resource name to the record ConfigMap its pod template reads.
    private static KubernetesResourceRecordStore CreateStore(FakeConfigMapApi api, Dictionary<string, string> keys) =>
        new(api, _ => ("appa", Owner), (_, resource) => keys.TryGetValue(resource.ToString(), out string? key) ? key : null);

    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);
}
