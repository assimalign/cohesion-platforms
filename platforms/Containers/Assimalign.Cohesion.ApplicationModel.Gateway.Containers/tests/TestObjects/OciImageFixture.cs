using System;
using System.Formats.Tar;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

internal sealed class OciImageFixture : IDisposable
{
    private const string digestPrefix = "sha256:";
    private readonly TestDirectory _directory = new();

    public OciImageFixture(
        string manifestMediaType = "application/vnd.oci.image.manifest.v1+json",
        string descriptorMediaType = "application/vnd.oci.image.manifest.v1+json")
    {
        Repository = "example/team/api";
        LayoutPath = _directory.CreateDirectory("layout");
        StorePath = _directory.GetPath("store");
        ArchivePath = _directory.GetPath("api.oci.tar");

        LayerBytes = Encoding.UTF8.GetBytes("synthetic OCI layer payload");
        LayerDigest = Digest(LayerBytes);
        ConfigBytes = Encoding.UTF8.GetBytes(
            $"{{\"architecture\":\"amd64\",\"os\":\"linux\",\"config\":{{}},\"rootfs\":{{\"type\":\"layers\",\"diff_ids\":[\"{LayerDigest}\"]}}}}");
        ConfigDigest = Digest(ConfigBytes);
        ManifestBytes = Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":2,\"mediaType\":{JsonSerializer.Serialize(manifestMediaType)},\"config\":{{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\"{ConfigDigest}\",\"size\":{ConfigBytes.Length}}},\"layers\":[{{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar\",\"digest\":\"{LayerDigest}\",\"size\":{LayerBytes.Length}}}]}}");
        ManifestDigest = Digest(ManifestBytes);
        IndexBytes = Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":2,\"manifests\":[{{\"mediaType\":{JsonSerializer.Serialize(descriptorMediaType)},\"digest\":\"{ManifestDigest}\",\"size\":{ManifestBytes.Length}}}]}}");

        WriteLayoutFile("oci-layout", Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}"));
        WriteLayoutFile("index.json", IndexBytes);
        WriteBlob(ConfigDigest, ConfigBytes);
        WriteBlob(LayerDigest, LayerBytes);
        WriteBlob(ManifestDigest, ManifestBytes);
        WriteArchive();
    }

    public string Repository { get; }

    public string LayoutPath { get; }

    public string StorePath { get; }

    public string ArchivePath { get; }

    public string ManifestDigest { get; }

    public string ConfigDigest { get; }

    public string LayerDigest { get; }

    public byte[] ManifestBytes { get; }

    public byte[] ConfigBytes { get; }

    public byte[] LayerBytes { get; }

    public byte[] IndexBytes { get; }

    public string BlobPath(string digest) => Path.Combine(
        LayoutPath,
        "blobs",
        "sha256",
        digest[digestPrefix.Length..]);

    public void CorruptBlob(string digest) =>
        File.WriteAllText(BlobPath(digest), "corrupt");

    public void Dispose() => _directory.Dispose();

    private static string Digest(byte[] bytes) =>
        digestPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void WriteBlob(string digest, byte[] content) =>
        WriteLayoutFile($"blobs/sha256/{digest[digestPrefix.Length..]}", content);

    private void WriteLayoutFile(string relativePath, byte[] content)
    {
        string path = Path.Combine(
            LayoutPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void WriteArchive()
    {
        using FileStream stream = File.Create(ArchivePath);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);
        WriteEntry(writer, "oci-layout", File.ReadAllBytes(Path.Combine(LayoutPath, "oci-layout")));
        WriteEntry(writer, "index.json", IndexBytes);
        WriteEntry(writer, $"blobs/sha256/{ConfigDigest[digestPrefix.Length..]}", ConfigBytes);
        WriteEntry(writer, $"blobs/sha256/{LayerDigest[digestPrefix.Length..]}", LayerBytes);
        WriteEntry(writer, $"blobs/sha256/{ManifestDigest[digestPrefix.Length..]}", ManifestBytes);
    }

    private static void WriteEntry(TarWriter writer, string name, byte[] content)
    {
        using var data = new MemoryStream(content, writable: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data,
        };
        writer.WriteEntry(entry);
    }
}
