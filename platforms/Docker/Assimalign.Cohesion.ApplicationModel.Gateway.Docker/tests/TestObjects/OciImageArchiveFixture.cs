using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

internal sealed class OciImageArchiveFixture : IDisposable
{
    public OciImageArchiveFixture(bool corruptLayer = false, bool gzip = false)
    {
        byte[] layer = Encoding.UTF8.GetBytes("synthetic Docker acquisition layer");
        LayerDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(layer));
        byte[] config = Encoding.UTF8.GetBytes(
            $"{{\"architecture\":\"amd64\",\"os\":\"linux\",\"config\":{{}},\"rootfs\":{{\"type\":\"layers\",\"diff_ids\":[\"{LayerDigest}\"]}}}}");
        ImageId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(config));
        byte[] manifest = Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"config\":{{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\"{ImageId}\",\"size\":{config.Length}}},\"layers\":[{{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar\",\"digest\":\"{LayerDigest}\",\"size\":{layer.Length}}}]}}");
        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifest));
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"cohesion-docker-{Guid.NewGuid():N}.tar{(gzip ? ".gz" : string.Empty)}");
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteArchive(writer, config, layer, manifest, corruptLayer);
        }

        tar.Position = 0;
        using FileStream stream = File.Create(Path);
        if (gzip)
        {
            using var compressed = new GZipStream(stream, CompressionLevel.SmallestSize);
            tar.CopyTo(compressed);
        }
        else
        {
            tar.CopyTo(stream);
        }
    }

    public string Path { get; }

    public string Digest { get; }

    public string ImageId { get; }

    public string LayerDigest { get; }

    public byte[] Content => File.ReadAllBytes(Path);

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    private void WriteArchive(
        TarWriter writer,
        byte[] config,
        byte[] layer,
        byte[] manifest,
        bool corruptLayer)
    {
        WriteFile(
            writer,
            "oci-layout",
            Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}"));
        WriteFile(
            writer,
            "index.json",
            Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":2,\"manifests\":[{{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\"{Digest}\",\"size\":{manifest.Length}}}]}}"));
        WriteFile(writer, $"blobs/sha256/{ImageId["sha256:".Length..]}", config);
        WriteFile(
            writer,
            $"blobs/sha256/{LayerDigest["sha256:".Length..]}",
            corruptLayer ? "corrupt"u8.ToArray() : layer);
        WriteFile(writer, $"blobs/sha256/{Digest["sha256:".Length..]}", manifest);
    }

    private static void WriteFile(TarWriter writer, string name, byte[] content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(content, writable: false),
        };
        writer.WriteEntry(entry);
        entry.DataStream.Dispose();
    }
}
