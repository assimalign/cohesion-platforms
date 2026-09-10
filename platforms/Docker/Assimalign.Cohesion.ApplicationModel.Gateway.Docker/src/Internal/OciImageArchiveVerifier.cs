using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal static class OciImageArchiveVerifier
{
    public static async Task<OciImageArchiveVerification> VerifyAsync(
        string archivePath,
        string digest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.Ordinal)
            || digest.Length != prefix.Length + 64)
        {
            throw new InvalidDataException(
                $"OCI archive verification requires a SHA-256 digest, not '{digest}'.");
        }

        string hexadecimal = digest[prefix.Length..].ToLowerInvariant();
        string manifestPath = $"blobs/sha256/{hexadecimal}";
        byte[]? indexBytes = null;
        byte[]? manifestBytes = null;
        await using Stream stream = OpenTarStream(archivePath);
        using var reader = new TarReader(stream, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Normalize(entry.Name);
            if (entry.DataStream is null)
            {
                continue;
            }

            if (string.Equals(name, "index.json", StringComparison.Ordinal))
            {
                indexBytes = await ReadAllAsync(entry.DataStream, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(name, manifestPath, StringComparison.Ordinal))
            {
                manifestBytes = await ReadAllAsync(entry.DataStream, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (indexBytes is null)
        {
            throw new InvalidDataException(
                $"Image archive '{archivePath}' is not an OCI image-layout archive: index.json is missing.");
        }

        if (!IndexContainsDigest(indexBytes, digest))
        {
            throw new InvalidDataException(
                $"OCI archive '{archivePath}' does not index expected manifest digest '{digest}'.");
        }

        if (manifestBytes is null
            || !string.Equals(
                prefix + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                digest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"OCI archive '{archivePath}' is missing manifest blob '{manifestPath}' or its content digest does not match '{digest}'.");
        }

        string imageId = ReadImageId(manifestBytes);
        await VerifyBlobAsync(archivePath, imageId, cancellationToken).ConfigureAwait(false);
        return new OciImageArchiveVerification(imageId);
    }

    private static string ReadImageId(byte[] manifest)
    {
        using JsonDocument document = JsonDocument.Parse(manifest.AsMemory());
        if (!document.RootElement.TryGetProperty("config", out JsonElement config)
            || config.ValueKind is not JsonValueKind.Object
            || !config.TryGetProperty("digest", out JsonElement digest)
            || digest.ValueKind is not JsonValueKind.String
            || digest.GetString() is not string value)
        {
            throw new InvalidDataException(
                "An OCI image manifest must contain a config descriptor with an immutable digest.");
        }

        RequireSha256Digest(value, "OCI image config");
        return value.ToLowerInvariant();
    }

    private static async Task VerifyBlobAsync(
        string archivePath,
        string digest,
        CancellationToken cancellationToken)
    {
        const string prefix = "sha256:";
        string blobPath = $"blobs/sha256/{digest[prefix.Length..]}";
        await using Stream stream = OpenTarStream(archivePath);
        using var reader = new TarReader(stream, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DataStream is null
                || !string.Equals(Normalize(entry.Name), blobPath, StringComparison.Ordinal))
            {
                continue;
            }

            byte[] hash = await SHA256.HashDataAsync(entry.DataStream, cancellationToken)
                .ConfigureAwait(false);
            string observed = prefix + Convert.ToHexStringLower(hash);
            if (!string.Equals(observed, digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"OCI archive '{archivePath}' image config blob '{blobPath}' does not match '{digest}'.");
            }

            return;
        }

        throw new InvalidDataException(
            $"OCI archive '{archivePath}' is missing image config blob '{blobPath}'.");
    }

    private static bool IndexContainsDigest(byte[] index, string digest)
    {
        using JsonDocument document = JsonDocument.Parse(index.AsMemory());
        if (!document.RootElement.TryGetProperty("manifests", out JsonElement manifests)
            || manifests.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("OCI index.json must contain a manifests array.");
        }

        foreach (JsonElement manifest in manifests.EnumerateArray())
        {
            if (manifest.ValueKind is JsonValueKind.Object
                && manifest.TryGetProperty("digest", out JsonElement value)
                && value.ValueKind is JsonValueKind.String
                && string.Equals(value.GetString(), digest, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireSha256Digest(string digest, string description)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.Ordinal)
            || digest.Length != prefix.Length + 64)
        {
            throw new InvalidDataException(
                $"{description} digest '{digest}' must be 'sha256:' followed by 64 hexadecimal characters.");
        }

        for (int index = prefix.Length; index < digest.Length; index++)
        {
            char character = digest[index];
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')
                and not (>= 'A' and <= 'F'))
            {
                throw new InvalidDataException(
                    $"{description} digest '{digest}' must be 'sha256:' followed by 64 hexadecimal characters.");
            }
        }
    }

    private static async Task<byte[]> ReadAllAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    private static Stream OpenTarStream(string archivePath)
    {
        var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        stream.Position = 0;
        if (first == 0x1f && second == 0x8b)
        {
            return new GZipStream(stream, CompressionMode.Decompress);
        }

        return stream;
    }

    private static string Normalize(string name)
    {
        string result = name.Replace('\\', '/');
        while (result.StartsWith("./", StringComparison.Ordinal))
        {
            result = result[2..];
        }

        return result.TrimStart('/');
    }
}

internal readonly record struct OciImageArchiveVerification(string ImageId);
