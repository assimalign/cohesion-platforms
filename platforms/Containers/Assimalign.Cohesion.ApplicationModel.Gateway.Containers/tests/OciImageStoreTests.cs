using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class OciImageStoreTests
{
    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should ingest and deduplicate a verified OCI archive")]
    public async Task IngestAsync_OnValidOciArchive_ShouldPersistVerifiedClosure()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);

        // Act
        await store.IngestAsync(
            fixture.Repository,
            fixture.ManifestDigest,
            fixture.ArchivePath,
            CancellationToken.None);
        await store.IngestAsync(
            fixture.Repository,
            fixture.ManifestDigest,
            fixture.ArchivePath,
            CancellationToken.None);

        // Assert
        string blobDirectory = Path.Combine(store.RootPath, "blobs", "sha256");
        string[] blobs = Directory.GetFiles(blobDirectory);
        blobs.Length.ShouldBe(3);
        File.ReadAllBytes(StoredBlobPath(store, fixture.ManifestDigest))
            .ShouldBe(fixture.ManifestBytes);
        File.ReadAllBytes(StoredBlobPath(store, fixture.ConfigDigest))
            .ShouldBe(fixture.ConfigBytes);
        File.ReadAllBytes(StoredBlobPath(store, fixture.LayerDigest))
            .ShouldBe(fixture.LayerBytes);
        Directory.GetFiles(
            Path.Combine(store.RootPath, "manifests"),
            "*.link",
            SearchOption.AllDirectories).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should tolerate concurrent directory ingestion without duplicate content")]
    public async Task IngestAsync_OnConcurrentDirectoryRequests_ShouldRemainDeterministic()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        IOciImageStore[] stores = Enumerable.Range(0, 4)
            .Select(_ => OciImageStores.Create(fixture.StorePath))
            .ToArray();

        // Act
        Task[] ingests = stores
            .Select(store => store.IngestAsync(
                fixture.Repository,
                fixture.ManifestDigest,
                fixture.LayoutPath,
                CancellationToken.None))
            .ToArray();
        await Task.WhenAll(ingests);

        // Assert
        IOciImageStore store = stores[0];
        Directory.GetFiles(Path.Combine(store.RootPath, "blobs", "sha256"))
            .Length.ShouldBe(3);
        Directory.GetFiles(
            Path.Combine(store.RootPath, "manifests"),
            "*.link",
            SearchOption.AllDirectories).ShouldHaveSingleItem();
        Directory.GetFiles(store.RootPath, "*.tmp", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should reject a blob whose bytes do not match its digest")]
    public async Task IngestAsync_OnCorruptBlob_ShouldRejectLayout()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        fixture.CorruptBlob(fixture.LayerDigest);
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.IngestAsync(
                fixture.Repository,
                fixture.ManifestDigest,
                fixture.LayoutPath,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(fixture.LayerDigest, Case.Sensitive);
        exception.Message.ShouldContain("hashes to", Case.Sensitive);
        Directory.Exists(Path.Combine(store.RootPath, "manifests"))
            .ShouldBeTrue();
        Directory.GetFiles(
            Path.Combine(store.RootPath, "manifests"),
            "*.link",
            SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should reject a missing source blob even when the store has that digest")]
    public async Task IngestAsync_OnMissingSourceBlobAndPreseededStore_ShouldRejectLayout()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        string hexadecimal = fixture.LayerDigest["sha256:".Length..];
        File.Delete(Path.Combine(fixture.LayoutPath, "blobs", "sha256", hexadecimal));
        string storeBlobDirectory = Path.Combine(fixture.StorePath, "blobs", "sha256");
        Directory.CreateDirectory(storeBlobDirectory);
        File.WriteAllBytes(
            Path.Combine(storeBlobDirectory, hexadecimal),
            Enumerable.Repeat((byte)0xff, fixture.LayerBytes.Length).ToArray());
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.IngestAsync(
                fixture.Repository,
                fixture.ManifestDigest,
                fixture.LayoutPath,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(fixture.LayerDigest, Case.Sensitive);
        exception.Message.ShouldContain("missing from the image layout", Case.Sensitive);
        Directory.GetFiles(
            Path.Combine(store.RootPath, "manifests"),
            "*.link",
            SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should require the advertised manifest digest")]
    public async Task IngestAsync_OnUnadvertisedManifestDigest_ShouldRejectLayout()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);
        string unknownDigest = "sha256:" + new string('f', 64);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.IngestAsync(
                fixture.Repository,
                unknownDigest,
                fixture.LayoutPath,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(unknownDigest, Case.Sensitive);
        exception.Message.ShouldContain("does not advertise", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should reject an unsupported manifest media type")]
    public async Task IngestAsync_OnUnsupportedManifestMediaType_ShouldRejectLayout()
    {
        // Arrange
        using var fixture = new OciImageFixture(
            "application/vnd.oci.image.manifest.v1+json\r\nX-Injected: yes");
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.IngestAsync(
                fixture.Repository,
                fixture.ManifestDigest,
                fixture.LayoutPath,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("mediaType", Case.Sensitive);
        exception.Message.ShouldContain("not supported", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should require matching descriptor and manifest media types")]
    public async Task IngestAsync_OnDifferentDescriptorMediaType_ShouldRejectLayout()
    {
        // Arrange
        using var fixture = new OciImageFixture(
            descriptorMediaType: "application/vnd.docker.distribution.manifest.v2+json");
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.IngestAsync(
                fixture.Repository,
                fixture.ManifestDigest,
                fixture.LayoutPath,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("does not match", Case.Sensitive);
        exception.Message.ShouldContain("mediaType", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - OCI store: Should ingest a Docker-save archive under its reconstructed digest")]
    public async Task IngestAsync_OnDockerSaveArchive_ShouldPersistVerifiedClosure()
    {
        // Arrange
        using var directory = new TestDirectory();
        const string repository = "example/team/api";
        byte[] config = "{\"architecture\":\"amd64\",\"os\":\"linux\"}"u8.ToArray();
        byte[] layer = "synthetic Docker-save layer"u8.ToArray();
        string configDigest = Digest(config);
        string layerDigest = Digest(layer);
        byte[] manifest = CreateDockerManifest(config, configDigest, layer, layerDigest);
        string manifestDigest = Digest(manifest);
        string archivePath = directory.GetPath("image.docker.tar");
        using (FileStream stream = File.Create(archivePath))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false))
        {
            WriteTarEntry(
                writer,
                "manifest.json",
                Encoding.UTF8.GetBytes(
                    $"[{{\"Config\":\"config.json\",\"RepoTags\":[\"{repository}:latest\"],\"Layers\":[\"layer.tar\"]}}]"));
            WriteTarEntry(writer, "config.json", config);
            WriteTarEntry(writer, "layer.tar", layer);
        }

        IOciImageStore store = OciImageStores.Create(directory.GetPath("store"));

        // Act
        await store.IngestAsync(
            repository,
            manifestDigest,
            archivePath,
            CancellationToken.None);

        // Assert
        File.ReadAllBytes(StoredBlobPath(store, manifestDigest)).ShouldBe(manifest);
        File.ReadAllBytes(StoredBlobPath(store, configDigest)).ShouldBe(config);
        File.ReadAllBytes(StoredBlobPath(store, layerDigest)).ShouldBe(layer);
    }

    private static string StoredBlobPath(IOciImageStore store, string digest) =>
        Path.Combine(store.RootPath, "blobs", "sha256", digest["sha256:".Length..]);

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] CreateDockerManifest(
        byte[] config,
        string configDigest,
        byte[] layer,
        string layerDigest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString(
                "mediaType",
                "application/vnd.docker.distribution.manifest.v2+json");
            writer.WritePropertyName("config");
            writer.WriteStartObject();
            writer.WriteString(
                "mediaType",
                "application/vnd.docker.container.image.v1+json");
            writer.WriteNumber("size", config.Length);
            writer.WriteString("digest", configDigest);
            writer.WriteEndObject();
            writer.WritePropertyName("layers");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString(
                "mediaType",
                "application/vnd.docker.image.rootfs.diff.tar");
            writer.WriteNumber("size", layer.Length);
            writer.WriteString("digest", layerDigest);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteTarEntry(TarWriter writer, string name, byte[] content)
    {
        using var data = new MemoryStream(content, writable: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data,
        };
        writer.WriteEntry(entry);
    }
}
