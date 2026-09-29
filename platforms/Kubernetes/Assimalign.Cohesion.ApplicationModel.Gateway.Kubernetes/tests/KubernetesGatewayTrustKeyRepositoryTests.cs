using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesGatewayTrustKeyRepositoryTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should persist one owned P-256 key and reload it")]
    public async Task LoadOrCreateAsync_OnMissingKey_ShouldPersistAndReloadOwnedKey()
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        var options = new KubernetesGatewayOptions { SystemNamespace = "gateway-system", FieldManager = "gateway-owner" };
        var repository = new KubernetesGatewayTrustKeyRepository(options, api);

        // Act
        using ECDsa first = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);
        using ECDsa loaded = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);

        // Assert
        first.KeySize.ShouldBe(256);
        first.ExportParameters(false).Curve.Oid.Value.ShouldBe(ECCurve.NamedCurves.nistP256.Oid.Value);
        first.ExportSubjectPublicKeyInfo().ShouldBe(loaded.ExportSubjectPublicKeyInfo());
        api.Creates.ShouldBe(1);
        api.Applies.ShouldBe(0);
        api.LastReadNamespace.ShouldBe("gateway-system");
        api.LastReadName.ShouldBe(KubernetesSystemInstallation.TrustName);
        V1Secret stored = api.Snapshot();
        stored.Type.ShouldBe("Opaque");
        stored.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation].ShouldBe("gateway-owner");
        stored.Metadata.Labels.ShouldNotContainKey(KubernetesMetadata.ResourceLabel);
        stored.Data.Keys.ShouldHaveSingleItem().ShouldBe("appa.root.p8");
        using var imported = ECDsa.Create();
        imported.ImportPkcs8PrivateKey(stored.Data["appa.root.p8"], out int bytesRead);
        bytesRead.ShouldBe(stored.Data["appa.root.p8"].Length);
        imported.ExportSubjectPublicKeyInfo().ShouldBe(first.ExportSubjectPublicKeyInfo());
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should isolate application and gateway identities during rotation")]
    public async Task RotateAsync_OnMultipleIdentities_ShouldReplaceOnlyRequestedEntry()
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);
        using ECDsa original = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);
        using ECDsa peer = await repository.LoadOrCreateAsync("appa", "peer", CancellationToken.None);
        using ECDsa other = await repository.LoadOrCreateAsync("appb", "root", CancellationToken.None);
        string version = api.Snapshot().Metadata.ResourceVersion;

        // Act
        using ECDsa rotated = await repository.RotateAsync("appa", "root", CancellationToken.None);
        using ECDsa peerLoaded = await repository.LoadOrCreateAsync("appa", "peer", CancellationToken.None);
        using ECDsa otherLoaded = await repository.LoadOrCreateAsync("appb", "root", CancellationToken.None);
        using ECDsa rotatedLoaded = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);

        // Assert
        rotated.ExportSubjectPublicKeyInfo().ShouldNotBe(original.ExportSubjectPublicKeyInfo());
        peer.ExportSubjectPublicKeyInfo().ShouldNotBe(other.ExportSubjectPublicKeyInfo());
        peerLoaded.ExportSubjectPublicKeyInfo().ShouldBe(peer.ExportSubjectPublicKeyInfo());
        otherLoaded.ExportSubjectPublicKeyInfo().ShouldBe(other.ExportSubjectPublicKeyInfo());
        rotatedLoaded.ExportSubjectPublicKeyInfo().ShouldBe(rotated.ExportSubjectPublicKeyInfo());
        api.Snapshot().Data.Count.ShouldBe(3);
        api.LastApplyResourceVersion.ShouldBe(version);
        api.LastApplyForce.ShouldBe(false);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should load the atomic creation winner")]
    public async Task LoadOrCreateAsync_OnCreateConflict_ShouldPreserveAndReturnWinner()
    {
        // Arrange
        using ECDsa winner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var api = new FakeGatewayTrustKeyApi();
        api.BeforeCreate = current =>
        {
            current.BeforeCreate = null;
            current.Seed(CreateSecret(new Dictionary<string, byte[]> { ["appa.root.p8"] = winner.ExportPkcs8PrivateKey() }));
        };
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        using ECDsa loaded = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);

        // Assert
        loaded.ExportSubjectPublicKeyInfo().ShouldBe(winner.ExportSubjectPublicKeyInfo());
        api.Creates.ShouldBe(1);
        api.Applies.ShouldBe(0);
        api.Reads.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should preserve a concurrent new identity when retrying an update")]
    public async Task LoadOrCreateAsync_OnUpdateConflict_ShouldReloadAndPreserveOtherEntry()
    {
        // Arrange
        using ECDsa peer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var api = new FakeGatewayTrustKeyApi();
        api.Seed(CreateSecret());
        api.BeforeApply = current =>
        {
            current.BeforeApply = null;
            current.Seed(CreateSecret(new Dictionary<string, byte[]> { ["appb.root.p8"] = peer.ExportPkcs8PrivateKey() }));
        };
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        using ECDsa created = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);
        using ECDsa loadedPeer = await repository.LoadOrCreateAsync("appb", "root", CancellationToken.None);

        // Assert
        api.Applies.ShouldBe(2);
        api.LastApplyResourceVersion.ShouldBe("2");
        api.LastApplyForce.ShouldBe(false);
        api.Snapshot().Data.Count.ShouldBe(2);
        api.Snapshot().Metadata.Labels.ShouldNotContainKey(KubernetesMetadata.ResourceLabel);
        loadedPeer.ExportSubjectPublicKeyInfo().ShouldBe(peer.ExportSubjectPublicKeyInfo());
        created.ExportSubjectPublicKeyInfo().ShouldNotBe(peer.ExportSubjectPublicKeyInfo());
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should reject foreign or unowned trust storage")]
    [InlineData("foreign-owner")]
    [InlineData(null)]
    public async Task LoadOrCreateAsync_OnForeignSecret_ShouldRefuseWithoutWriting(string? owner)
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        V1Secret secret = CreateSecret();
        if (owner is null)
        {
            secret.Metadata.Annotations.Clear();
        }
        else
        {
            secret.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation] = owner;
        }

        api.Seed(secret);
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => repository.LoadOrCreateAsync("appa", "root", CancellationToken.None));

        // Assert
        error.Message.ShouldContain("cannot be adopted", Case.Sensitive);
        api.Creates.ShouldBe(0);
        api.Applies.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should reject unreadable or non-P-256 keys without replacement")]
    [InlineData("empty", false)]
    [InlineData("corrupt", false)]
    [InlineData("trailing", false)]
    [InlineData("wrong-curve", false)]
    [InlineData("wrong-kind", false)]
    [InlineData("corrupt", true)]
    [InlineData("wrong-curve", true)]
    public async Task LoadOrCreateAsync_OnInvalidStoredKey_ShouldFailClosed(string shape, bool rotate)
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        byte[] content = CreateInvalidKey(shape);
        api.Seed(CreateSecret(new Dictionary<string, byte[]> { ["appa.root.p8"] = content }));
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() => rotate
            ? repository.RotateAsync("appa", "root", CancellationToken.None)
            : repository.LoadOrCreateAsync("appa", "root", CancellationToken.None));

        // Assert
        error.Message.ShouldContain("appa.root.p8", Case.Sensitive);
        api.Creates.ShouldBe(0);
        api.Applies.ShouldBe(0);
        CryptographicOperations.FixedTimeEquals(api.Snapshot().Data["appa.root.p8"], content).ShouldBe(true);
        CryptographicOperations.ZeroMemory(content);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should bound repeated storage conflicts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadOrCreateAsync_OnPersistentConflicts_ShouldStopAfterBoundedAttempts(bool existing)
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi { ConflictEveryCreate = !existing, ConflictEveryApply = existing };
        if (existing)
        {
            api.Seed(CreateSecret());
        }

        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => repository.LoadOrCreateAsync("appa", "root", CancellationToken.None));

        // Assert
        error.Message.ShouldContain("8 concurrent update attempts", Case.Sensitive);
        api.Reads.ShouldBe(8);
        api.Creates.ShouldBe(existing ? 0 : 8);
        api.Applies.ShouldBe(existing ? 8 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should validate identity labels before storage access")]
    [InlineData("app.a", "root")]
    [InlineData("appa", "root.peer")]
    [InlineData("appa", "../root")]
    public async Task LoadOrCreateAsync_OnInvalidIdentity_ShouldRejectBeforeStorageAccess(string application, string gateway)
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => repository.LoadOrCreateAsync(application, gateway, CancellationToken.None));

        // Assert
        api.Reads.ShouldBe(0);
        api.Creates.ShouldBe(0);
        api.Applies.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should use the gateway namespace and resource normalization")]
    public async Task LoadOrCreateAsync_OnCaseVariantIdentity_ShouldReloadSamePlatformIdentity()
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);

        // Act
        using ECDsa first = await repository.LoadOrCreateAsync("AppA", "Root", CancellationToken.None);
        using ECDsa loaded = await repository.LoadOrCreateAsync("appa", "root", CancellationToken.None);

        // Assert
        loaded.ExportSubjectPublicKeyInfo().ShouldBe(first.ExportSubjectPublicKeyInfo());
        api.Snapshot().Data.Keys.ShouldHaveSingleItem().ShouldBe("appa.root.p8");
        api.Creates.ShouldBe(1);
        api.Applies.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust keys: Should honor cancellation before storage access")]
    public async Task LoadOrCreateAsync_OnCancellation_ShouldAvoidStorageAccess()
    {
        // Arrange
        using var api = new FakeGatewayTrustKeyApi();
        var repository = new KubernetesGatewayTrustKeyRepository(new KubernetesGatewayOptions(), api);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(
            () => repository.LoadOrCreateAsync("appa", "root", cancellation.Token));

        // Assert
        api.Reads.ShouldBe(0);
        api.Creates.ShouldBe(0);
    }

    private static V1Secret CreateSecret(IDictionary<string, byte[]>? data = null) => new()
    {
        ApiVersion = "v1",
        Kind = "Secret",
        Type = "Opaque",
        Metadata = KubernetesMetadata.CreateObjectMeta(
            KubernetesSystemInstallation.TrustName, "cohesion-system", "cohesion-gateway", "system/v1", "cohesion-gateway"),
        Data = data ?? new Dictionary<string, byte[]>(),
    };

    private static byte[] CreateInvalidKey(string shape)
    {
        if (shape is "empty")
        {
            return [];
        }

        if (shape is "corrupt")
        {
            return [1, 2, 3];
        }

        if (shape is "wrong-kind")
        {
            using RSA rsa = RSA.Create(2048);
            return rsa.ExportPkcs8PrivateKey();
        }

        using ECDsa key = ECDsa.Create(shape is "wrong-curve" ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
        byte[] content = key.ExportPkcs8PrivateKey();
        if (shape is "trailing")
        {
            byte[] trailing = [.. content, 0];
            CryptographicOperations.ZeroMemory(content);
            return trailing;
        }

        return content;
    }
}
