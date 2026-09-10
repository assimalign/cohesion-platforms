using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class OciImageStore : IOciImageStore
{
    private const string digestPrefix = "sha256:";
    private const string ociManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string dockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    private readonly SemaphoreSlim _ingestGate = new(1, 1);

    public OciImageStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public async Task IngestAsync(
        IContainerImageIndexEntry image,
        string imageIndexPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        string archivePath = ContainerImageIndexes.ResolveArchivePath(imageIndexPath, image)
            ?? throw new InvalidDataException(
                $"Image index entry for resource '{image.Resource}' has no archivePath to ingest.");
        await IngestAsync(
            image.Repository,
            image.Digest,
            archivePath,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task IngestAsync(
        string repository,
        string digest,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        _ = ContainerImageArtifacts.Create(default, $"{repository}@{digest}");

        string sourcePath = Path.GetFullPath(archivePath);
        await _ingestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(BlobDirectory);
            Directory.CreateDirectory(ManifestDirectory);
            if (Directory.Exists(sourcePath))
            {
                await IngestExtractedAsync(
                    FindContentRoot(sourcePath),
                    repository,
                    digest.ToLowerInvariant(),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Container image archive was not found.", sourcePath);
            }

            string stagingRoot = Path.Combine(RootPath, ".ingest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);
            try
            {
                await ExtractArchiveAsync(sourcePath, stagingRoot, cancellationToken)
                    .ConfigureAwait(false);
                await IngestExtractedAsync(
                    FindContentRoot(stagingRoot),
                    repository,
                    digest.ToLowerInvariant(),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
        }
        finally
        {
            _ingestGate.Release();
        }
    }

    internal bool TryGetManifest(
        string repository,
        string digest,
        out StoredImageContent content)
    {
        content = default;
        if (!ContainerImageValidation.IsRepository(repository)
            || !ContainerImageValidation.TryNormalizeDigest(digest, out string normalized))
        {
            return false;
        }

        string link = GetManifestLinkPath(repository, normalized);
        string blob = GetBlobPath(normalized);
        if (!File.Exists(link)
            || !File.Exists(blob)
            || !ManifestLinkContains(link, normalized))
        {
            return false;
        }

        content = new StoredImageContent(
            blob,
            normalized,
            ReadManifestMediaType(blob),
            new FileInfo(blob).Length);
        return true;
    }

    internal bool TryGetBlob(
        string repository,
        string digest,
        out StoredImageContent content)
    {
        content = default;
        if (!ContainerImageValidation.IsRepository(repository)
            || !ContainerImageValidation.TryNormalizeDigest(digest, out string normalized))
        {
            return false;
        }

        string repositoryDirectory = Path.GetDirectoryName(GetManifestLinkPath(repository, normalized))!;
        if (!Directory.Exists(repositoryDirectory))
        {
            return false;
        }

        bool reachable = false;
        string[] links = Directory.GetFiles(repositoryDirectory, "*.link");
        for (int linkIndex = 0; linkIndex < links.Length && !reachable; linkIndex++)
        {
            foreach (string candidate in File.ReadLines(links[linkIndex]))
            {
                if (string.Equals(candidate, normalized, StringComparison.Ordinal))
                {
                    reachable = true;
                    break;
                }
            }
        }

        return reachable && TryGetBlob(normalized, out content);
    }

    private static bool ManifestLinkContains(string path, string digest)
    {
        foreach (string candidate in File.ReadLines(path))
        {
            if (string.Equals(candidate, digest, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetBlob(string digest, out StoredImageContent content)
    {
        content = default;
        if (!ContainerImageValidation.TryNormalizeDigest(digest, out string normalized))
        {
            return false;
        }

        string blob = GetBlobPath(normalized);
        if (!File.Exists(blob))
        {
            return false;
        }

        content = new StoredImageContent(
            blob,
            normalized,
            "application/octet-stream",
            new FileInfo(blob).Length);
        return true;
    }

    private string BlobDirectory => Path.Combine(RootPath, "blobs", "sha256");

    private string ManifestDirectory => Path.Combine(RootPath, "manifests");

    private async Task IngestExtractedAsync(
        string contentRoot,
        string repository,
        string digest,
        CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(contentRoot, "index.json");
        if (File.Exists(indexPath))
        {
            await IngestOciLayoutAsync(
                contentRoot,
                repository,
                digest,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string dockerManifestPath = Path.Combine(contentRoot, "manifest.json");
        if (File.Exists(dockerManifestPath))
        {
            await IngestDockerSaveAsync(
                contentRoot,
                repository,
                digest,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException(
            $"Image content '{contentRoot}' contains neither OCI index.json nor Docker-save manifest.json.");
    }

    private async Task IngestOciLayoutAsync(
        string contentRoot,
        string repository,
        string digest,
        CancellationToken cancellationToken)
    {
        string layoutPath = Path.Combine(contentRoot, "oci-layout");
        if (!File.Exists(layoutPath))
        {
            throw new InvalidDataException("An OCI image layout must contain oci-layout.");
        }

        using (JsonDocument layout = await ReadJsonAsync(layoutPath, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!layout.RootElement.TryGetProperty("imageLayoutVersion", out JsonElement version)
                || version.ValueKind is not JsonValueKind.String
                || !string.Equals(version.GetString(), "1.0.0", StringComparison.Ordinal))
            {
                throw new InvalidDataException("OCI oci-layout imageLayoutVersion must equal '1.0.0'.");
            }
        }

        string indexPath = Path.Combine(contentRoot, "index.json");
        using JsonDocument index = await ReadJsonAsync(indexPath, cancellationToken)
            .ConfigureAwait(false);
        JsonElement manifestDescriptor = FindManifestDescriptor(index.RootElement, digest);

        string sourceBlobDirectory = Path.Combine(contentRoot, "blobs", "sha256");
        if (!Directory.Exists(sourceBlobDirectory))
        {
            throw new InvalidDataException("OCI image layout blobs/sha256 directory is missing.");
        }

        string[] blobs = Directory.GetFiles(sourceBlobDirectory);
        for (int blobIndex = 0; blobIndex < blobs.Length; blobIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hexadecimal = Path.GetFileName(blobs[blobIndex]);
            string candidateDigest = $"{digestPrefix}{hexadecimal}";
            if (!ContainerImageValidation.TryNormalizeDigest(candidateDigest, out string normalized)
                || !string.Equals(normalized, candidateDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"OCI blob filename '{hexadecimal}' is not a lowercase SHA-256 digest.");
            }

            await VerifyAndStoreFileAsync(blobs[blobIndex], candidateDigest, cancellationToken)
                .ConfigureAwait(false);
        }

        RequireSourceBlob(sourceBlobDirectory, digest, "image manifest");
        StoredImageContent manifest = RequireStoredBlob(digest, "image manifest");
        long descriptorSize = RequireDescriptorSize(manifestDescriptor, "OCI index manifest");
        if (descriptorSize != manifest.Length)
        {
            throw new InvalidDataException(
                $"OCI index manifest size {descriptorSize} does not match blob size {manifest.Length}.");
        }

        using JsonDocument manifestDocument = await ReadJsonAsync(manifest.Path, cancellationToken)
            .ConfigureAwait(false);
        string manifestMediaType = ValidateImageManifest(manifestDocument.RootElement);
        string descriptorMediaType = RequireDescriptorMediaType(
            manifestDescriptor,
            "OCI index manifest");
        if (!string.Equals(manifestMediaType, descriptorMediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"OCI index manifest mediaType '{descriptorMediaType}' does not match " +
                $"image manifest mediaType '{manifestMediaType}'.");
        }

        var closure = new List<string> { digest };
        closure.Add(ValidateDescriptorBlob(
            manifestDocument.RootElement,
            "config",
            sourceBlobDirectory));
        if (!manifestDocument.RootElement.TryGetProperty("layers", out JsonElement layers)
            || layers.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("OCI image manifest must contain a layers array.");
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            closure.Add(ValidateDescriptorBlob(layer, null, sourceBlobDirectory));
        }

        await WriteManifestLinkAsync(repository, digest, closure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task IngestDockerSaveAsync(
        string contentRoot,
        string repository,
        string digest,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await ReadJsonAsync(
            Path.Combine(contentRoot, "manifest.json"),
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("Docker-save manifest.json must be an array.");
        }

        JsonElement record = FindDockerSaveRecord(document.RootElement, repository);
        string configName = RequireRelativeString(record, "Config", "Docker-save config");
        string configPath = ResolveContentPath(contentRoot, configName);
        StoredImageContent config = await VerifyAndStoreUnknownFileAsync(configPath, cancellationToken)
            .ConfigureAwait(false);
        if (!record.TryGetProperty("Layers", out JsonElement layersElement)
            || layersElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("Docker-save record must contain a Layers array.");
        }

        var layers = new List<DockerSaveLayer>();
        foreach (JsonElement layerElement in layersElement.EnumerateArray())
        {
            if (layerElement.ValueKind is not JsonValueKind.String
                || layerElement.GetString() is not string layerName
                || string.IsNullOrWhiteSpace(layerName))
            {
                throw new InvalidDataException("Docker-save Layers entries must be non-empty strings.");
            }

            string layerPath = ResolveContentPath(contentRoot, layerName);
            StoredImageContent layer = await VerifyAndStoreUnknownFileAsync(
                layerPath,
                cancellationToken).ConfigureAwait(false);
            layers.Add(new DockerSaveLayer(layer, IsGzip(layerPath)));
        }

        byte[] manifestBytes = CreateDockerManifest(config, layers);
        string observedDigest = digestPrefix + Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (!string.Equals(observedDigest, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Docker-save archive reconstructs manifest digest '{observedDigest}', not expected '{digest}'.");
        }

        await StoreBytesAsync(manifestBytes, digest, cancellationToken).ConfigureAwait(false);
        var closure = new List<string>(layers.Count + 2) { digest, config.Digest };
        for (int index = 0; index < layers.Count; index++)
        {
            closure.Add(layers[index].Content.Digest);
        }

        await WriteManifestLinkAsync(repository, digest, closure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifyAndStoreFileAsync(
        string sourcePath,
        string digest,
        CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(BlobDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string observed = await CopyAndHashAsync(sourcePath, temporary, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(observed, digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Blob '{sourcePath}' hashes to '{observed}', not advertised digest '{digest}'.");
            }

            await CommitTemporaryBlobAsync(temporary, digest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<StoredImageContent> VerifyAndStoreUnknownFileAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(BlobDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string digest = await CopyAndHashAsync(sourcePath, temporary, cancellationToken)
                .ConfigureAwait(false);
            await CommitTemporaryBlobAsync(temporary, digest, cancellationToken).ConfigureAwait(false);
            return RequireStoredBlob(digest, "Docker-save blob");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task CommitTemporaryBlobAsync(
        string temporary,
        string digest,
        CancellationToken cancellationToken)
    {
        string destination = GetBlobPath(digest);
        try
        {
            if (File.Exists(destination))
            {
                await VerifyStoredFileAsync(destination, digest, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            await VerifyStoredFileAsync(destination, digest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072];
        await using FileStream source = OpenRead(sourcePath);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: buffer.Length,
            useAsync: true);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return digestPrefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task StoreBytesAsync(
        byte[] bytes,
        string digest,
        CancellationToken cancellationToken)
    {
        string destination = GetBlobPath(digest);
        if (File.Exists(destination))
        {
            await VerifyStoredFileAsync(destination, digest, cancellationToken).ConfigureAwait(false);
            return;
        }

        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            File.Delete(temporary);
            await VerifyStoredFileAsync(destination, digest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task VerifyStoredFileAsync(
        string path,
        string digest,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string observed = digestPrefix + Convert.ToHexStringLower(hash);
        if (!string.Equals(observed, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Stored blob '{path}' is corrupt: observed '{observed}', expected '{digest}'.");
        }
    }

    private string ValidateDescriptorBlob(
        JsonElement owner,
        string? property,
        string sourceBlobDirectory)
    {
        JsonElement descriptor = property is null
            ? owner
            : owner.TryGetProperty(property, out JsonElement value)
                ? value
                : throw new InvalidDataException(
                    $"OCI image manifest must contain a {property} descriptor.");
        string digest = RequireDescriptorDigest(descriptor, property ?? "layer");
        RequireSourceBlob(sourceBlobDirectory, digest, property ?? "layer");
        StoredImageContent blob = RequireStoredBlob(digest, property ?? "layer");
        long size = RequireDescriptorSize(descriptor, property ?? "layer");
        if (size != blob.Length)
        {
            throw new InvalidDataException(
                $"OCI {property ?? "layer"} descriptor size {size} does not match blob size {blob.Length}.");
        }

        return digest;
    }

    private static void RequireSourceBlob(
        string sourceBlobDirectory,
        string digest,
        string description)
    {
        string sourcePath = Path.Combine(
            sourceBlobDirectory,
            digest[digestPrefix.Length..]);
        if (!File.Exists(sourcePath))
        {
            throw new InvalidDataException(
                $"OCI {description} blob '{digest}' is missing from the image layout.");
        }
    }

    private StoredImageContent RequireStoredBlob(string digest, string description)
    {
        if (!TryGetBlob(digest, out StoredImageContent content))
        {
            throw new InvalidDataException(
                $"OCI {description} blob '{digest}' is missing from the content store.");
        }

        return content;
    }

    private async Task WriteManifestLinkAsync(
        string repository,
        string digest,
        IReadOnlyList<string> closure,
        CancellationToken cancellationToken)
    {
        string link = GetManifestLinkPath(repository, digest);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        string temporary = $"{link}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(temporary, closure, Encoding.ASCII, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(temporary, link);
            }
            catch (IOException) when (File.Exists(link))
            {
                await RequireEquivalentManifestLinkAsync(link, closure, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (File.Exists(link))
            {
                await RequireEquivalentManifestLinkAsync(link, closure, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task RequireEquivalentManifestLinkAsync(
        string path,
        IReadOnlyList<string> expected,
        CancellationToken cancellationToken)
    {
        string[] existing = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        bool equivalent = existing.Length == expected.Count;
        for (int index = 0; equivalent && index < existing.Length; index++)
        {
            equivalent = string.Equals(existing[index], expected[index], StringComparison.Ordinal);
        }

        if (!equivalent)
        {
            throw new InvalidDataException(
                $"Content-store manifest link '{path}' conflicts with an existing image closure.");
        }
    }

    private string GetBlobPath(string digest) =>
        Path.Combine(BlobDirectory, digest[digestPrefix.Length..]);

    private string GetManifestLinkPath(string repository, string digest)
    {
        string repositoryKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(repository)));
        return Path.Combine(
            ManifestDirectory,
            repositoryKey,
            $"{digest[digestPrefix.Length..]}.link");
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenRead(archivePath);
        byte[] signature = new byte[2];
        int signatureLength = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        GZipStream? gzip = signatureLength == 2 && signature[0] == 0x1f && signature[1] == 0x8b
            ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true)
            : null;
        Stream tarStream = gzip is null ? stream : gzip;
        using (gzip)
        using (var reader = new TarReader(tarStream, leaveOpen: true))
        {
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)
                .ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType is TarEntryType.Directory)
                {
                    continue;
                }

                if (entry.EntryType is not TarEntryType.RegularFile
                    and not TarEntryType.V7RegularFile
                    and not TarEntryType.ContiguousFile)
                {
                    throw new InvalidDataException(
                        $"Image archive entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");
                }

                string relative = NormalizeArchiveName(entry.Name);
                string target = ResolveContentPath(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.DataStream is null)
                {
                    throw new InvalidDataException(
                        $"Image archive entry '{entry.Name}' has no content stream.");
                }

                await using FileStream output = new(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 131072,
                    useAsync: true);
                await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string FindContentRoot(string root)
    {
        if (File.Exists(Path.Combine(root, "index.json"))
            || File.Exists(Path.Combine(root, "manifest.json")))
        {
            return root;
        }

        string[] directories = Directory.GetDirectories(root);
        string[] files = Directory.GetFiles(root);
        if (files.Length == 0 && directories.Length == 1
            && (File.Exists(Path.Combine(directories[0], "index.json"))
                || File.Exists(Path.Combine(directories[0], "manifest.json"))))
        {
            return directories[0];
        }

        return root;
    }

    private static JsonElement FindManifestDescriptor(JsonElement index, string digest)
    {
        if (!index.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
            || schemaVersion.ValueKind is not JsonValueKind.Number
            || schemaVersion.GetInt32() != 2
            || !index.TryGetProperty("manifests", out JsonElement manifests)
            || manifests.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "OCI index.json must have schemaVersion 2 and a manifests array.");
        }

        foreach (JsonElement descriptor in manifests.EnumerateArray())
        {
            if (string.Equals(
                RequireDescriptorDigest(descriptor, "index manifest"),
                digest,
                StringComparison.Ordinal))
            {
                return descriptor.Clone();
            }
        }

        throw new InvalidDataException(
            $"OCI index.json does not advertise expected manifest digest '{digest}'.");
    }

    private static string ValidateImageManifest(JsonElement manifest)
    {
        if (manifest.ValueKind is not JsonValueKind.Object
            || !manifest.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
            || schemaVersion.ValueKind is not JsonValueKind.Number
            || schemaVersion.GetInt32() != 2)
        {
            throw new InvalidDataException("OCI image manifest schemaVersion must equal 2.");
        }

        string mediaType = RequireDescriptorMediaType(manifest, "image manifest");
        if (!string.Equals(mediaType, ociManifestMediaType, StringComparison.Ordinal)
            && !string.Equals(mediaType, dockerManifestMediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"OCI image manifest mediaType '{mediaType}' is not supported.");
        }

        return mediaType;
    }

    private static string RequireDescriptorMediaType(
        JsonElement descriptor,
        string description)
    {
        if (descriptor.ValueKind is not JsonValueKind.Object
            || !descriptor.TryGetProperty("mediaType", out JsonElement value)
            || value.ValueKind is not JsonValueKind.String
            || value.GetString() is not string mediaType
            || string.IsNullOrWhiteSpace(mediaType))
        {
            throw new InvalidDataException(
                $"{description} must contain a non-empty mediaType.");
        }

        return mediaType;
    }

    private static string RequireDescriptorDigest(JsonElement descriptor, string description)
    {
        if (descriptor.ValueKind is not JsonValueKind.Object
            || !descriptor.TryGetProperty("digest", out JsonElement value)
            || value.ValueKind is not JsonValueKind.String
            || !ContainerImageValidation.TryNormalizeDigest(value.GetString(), out string digest))
        {
            throw new InvalidDataException(
                $"OCI {description} descriptor must contain a SHA-256 digest.");
        }

        return digest;
    }

    private static long RequireDescriptorSize(JsonElement descriptor, string description)
    {
        if (!descriptor.TryGetProperty("size", out JsonElement size)
            || size.ValueKind is not JsonValueKind.Number
            || !size.TryGetInt64(out long value)
            || value < 0)
        {
            throw new InvalidDataException(
                $"OCI {description} descriptor must contain a non-negative size.");
        }

        return value;
    }

    private static JsonElement FindDockerSaveRecord(JsonElement records, string repository)
    {
        JsonElement? only = null;
        int count = 0;
        foreach (JsonElement record in records.EnumerateArray())
        {
            count++;
            only = record.Clone();
            if (!record.TryGetProperty("RepoTags", out JsonElement tags)
                || tags.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement tag in tags.EnumerateArray())
            {
                if (tag.ValueKind is JsonValueKind.String
                    && tag.GetString() is string value
                    && (string.Equals(value, repository, StringComparison.Ordinal)
                        || value.StartsWith($"{repository}:", StringComparison.Ordinal)))
                {
                    return record.Clone();
                }
            }
        }

        if (count == 1 && only is JsonElement single)
        {
            return single;
        }

        throw new InvalidDataException(
            $"Docker-save manifest.json has no unique record for repository '{repository}'.");
    }

    private static string RequireRelativeString(
        JsonElement owner,
        string property,
        string description)
    {
        if (!owner.TryGetProperty(property, out JsonElement value)
            || value.ValueKind is not JsonValueKind.String
            || value.GetString() is not string result
            || string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"{description} must be a non-empty relative path.");
        }

        return result;
    }

    private static byte[] CreateDockerManifest(
        StoredImageContent config,
        IReadOnlyList<DockerSaveLayer> layers)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("mediaType", dockerManifestMediaType);
            writer.WritePropertyName("config");
            writer.WriteStartObject();
            writer.WriteString("mediaType", "application/vnd.docker.container.image.v1+json");
            writer.WriteNumber("size", config.Length);
            writer.WriteString("digest", config.Digest);
            writer.WriteEndObject();
            writer.WritePropertyName("layers");
            writer.WriteStartArray();
            for (int index = 0; index < layers.Count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "mediaType",
                    layers[index].Gzip
                        ? "application/vnd.docker.image.rootfs.diff.tar.gzip"
                        : "application/vnd.docker.image.rootfs.diff.tar");
                writer.WriteNumber("size", layers[index].Content.Length);
                writer.WriteString("digest", layers[index].Content.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static bool IsGzip(string path)
    {
        using FileStream stream = File.OpenRead(path);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        return first == 0x1f && second == 0x8b;
    }

    private static string ResolveContentPath(string root, string relative)
    {
        if (!ContainerImageValidation.IsRelativeArchivePath(relative))
        {
            throw new InvalidDataException(
                $"Image archive path '{relative}' must be relative.");
        }

        string fullRoot = Path.GetFullPath(root);
        string normalized = ContainerImageValidation.NormalizePathSeparators(relative);
        if (!ContainerImageValidation.TryResolveContainedPath(
                fullRoot,
                normalized,
                out string result))
        {
            throw new InvalidDataException(
                $"Image archive path '{relative}' escapes its archive root.");
        }

        return result;
    }

    private static string NormalizeArchiveName(string name)
    {
        string result = name.Replace('\\', '/');
        while (result.StartsWith("./", StringComparison.Ordinal))
        {
            result = result[2..];
        }

        if (!ContainerImageValidation.IsRelativeArchivePath(result))
        {
            throw new InvalidDataException($"Image archive entry '{name}' is not a relative file path.");
        }

        return result;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenRead(path);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Image JSON '{path}' is invalid: {exception.Message}", exception);
        }
    }

    private static string ReadManifestMediaType(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        return ValidateImageManifest(document.RootElement);
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 131072,
        useAsync: true);

    private readonly record struct DockerSaveLayer(StoredImageContent Content, bool Gzip);
}

internal readonly record struct StoredImageContent(
    string Path,
    string Digest,
    string MediaType,
    long Length);
