using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Reads, validates, and resolves Cohesion resource and application image indexes.
/// </summary>
public static class ContainerImageIndexes
{
    /// <summary>The exact image-index schema identifier supported by this package.</summary>
    public const string Schema = "cohesion/images/v1";

    /// <summary>The version 1 marker indicating that a target supplies the registry authority.</summary>
    public const string LateBoundRegistry = "<late-bound>";

    /// <summary>
    /// Reads and validates one resource's <c>image.json</c> document.
    /// </summary>
    /// <param name="path">The image-index document path.</param>
    /// <param name="cancellationToken">Signals that reading should stop.</param>
    /// <returns>The validated image entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The document does not conform to <see cref="Schema"/>.</exception>
    /// <exception cref="IOException">The document cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async Task<IContainerImageIndexEntry> ReadImageAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = OpenRead(path);
        ImageIndexDocument document = await DeserializeAsync(
            stream,
            ImageIndexJsonContext.Default.ImageIndexDocument,
            path,
            cancellationToken).ConfigureAwait(false);
        RequireSchema(document.Schema, path);
        return ValidateEntry(
            document.Resource,
            document.Repository,
            document.Digest,
            document.Tag,
            document.ArchivePath,
            document.Aot,
            document.BaseImage,
            document.Registry,
            path);
    }

    /// <summary>
    /// Reads and validates an application's <c>application.images.json</c> document.
    /// </summary>
    /// <param name="path">The application image-index document path.</param>
    /// <param name="cancellationToken">Signals that reading should stop.</param>
    /// <returns>The validated application image index.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The document does not conform to <see cref="Schema"/>.</exception>
    /// <exception cref="IOException">The document cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async Task<IApplicationImageIndex> ReadApplicationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = OpenRead(path);
        ApplicationImageIndexDocument document = await DeserializeAsync(
            stream,
            ImageIndexJsonContext.Default.ApplicationImageIndexDocument,
            path,
            cancellationToken).ConfigureAwait(false);
        RequireSchema(document.Schema, path);
        if (string.IsNullOrWhiteSpace(document.Application))
        {
            throw Invalid(path, "application must be a non-empty string.");
        }

        if (document.Images is null)
        {
            throw Invalid(path, "images is required and must be a JSON array.");
        }

        var resources = new HashSet<string>(StringComparer.Ordinal);
        var images = new IContainerImageIndexEntry[document.Images.Count];
        for (int index = 0; index < document.Images.Count; index++)
        {
            ApplicationImageIndexEntryDocument entry = document.Images[index]
                ?? throw Invalid(path, $"images[{index}] must not be null.");
            IContainerImageIndexEntry image = ValidateEntry(
                entry.Resource,
                entry.Repository,
                entry.Digest,
                entry.Tag,
                entry.ArchivePath,
                entry.Aot,
                entry.BaseImage,
                entry.Registry,
                $"{path} images[{index}]");
            if (!resources.Add(image.Resource.Value))
            {
                throw Invalid(path, $"resource '{image.Resource}' occurs more than once.");
            }

            images[index] = image;
        }

        return new ApplicationImageIndex(
            ApplicationName.Parse(document.Application),
            new ReadOnlyCollection<IContainerImageIndexEntry>(images));
    }

    /// <summary>
    /// Resolves the owning resource's entry for the version 1 <see cref="ArtifactRef.Self"/>
    /// artifact reference.
    /// </summary>
    /// <param name="index">The application image index.</param>
    /// <param name="resource">The resource whose own image is required.</param>
    /// <param name="artifact">The plan artifact reference.</param>
    /// <returns>The resource's unique image entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="artifact"/> is not <see cref="ArtifactRef.Self"/>, or the index has no
    /// entry owned by <paramref name="resource"/>.
    /// </exception>
    public static IContainerImageIndexEntry Resolve(
        IApplicationImageIndex index,
        ResourceName resource,
        ArtifactRef artifact)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (artifact != ArtifactRef.Self)
        {
            throw new InvalidDataException(
                $"Image schema '{Schema}' supports only artifact '{ArtifactRef.Self}', not '{artifact}'.");
        }

        for (int imageIndex = 0; imageIndex < index.Images.Count; imageIndex++)
        {
            IContainerImageIndexEntry image = index.Images[imageIndex];
            if (image.Resource == resource)
            {
                return image;
            }
        }

        throw new InvalidDataException(
            $"Application '{index.Application}' image index has no own image entry for resource '{resource}' required by artifact '{ArtifactRef.Self}'.");
    }

    /// <summary>
    /// Creates a digest-pinned artifact from an index entry, applying a target registry only when
    /// the entry declares <c>&lt;late-bound&gt;</c>.
    /// </summary>
    /// <param name="resource">The runtime identifier of the entry's owning resource.</param>
    /// <param name="entry">The validated image entry.</param>
    /// <param name="registry">An optional target registry authority such as <c>registry.example.test:5000</c>.</param>
    /// <returns>The immutable image artifact.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="registry"/> is not a registry authority.</exception>
    public static IContainerImageArtifact CreateArtifact(
        ResourceId resource,
        IContainerImageIndexEntry entry,
        string? registry = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string repository = entry.Repository;
        if (string.Equals(entry.Registry, LateBoundRegistry, StringComparison.Ordinal)
            && registry is not null)
        {
            if (!ContainerImageValidation.IsRegistryAuthority(registry))
            {
                throw new ArgumentException(
                    "A late-bound container registry must be an authority without a URI scheme or path.",
                    nameof(registry));
            }

            repository = $"{registry}/{repository}";
        }

        return ContainerImageArtifacts.Create(
            resource,
            $"{repository}@{entry.Digest}",
            entry.Tag);
    }

    /// <summary>
    /// Resolves an entry's optional archive path relative to its index document without allowing
    /// the path to escape the index directory.
    /// </summary>
    /// <param name="indexPath">The path of <c>image.json</c> or <c>application.images.json</c>.</param>
    /// <param name="entry">The validated image entry.</param>
    /// <returns>The absolute archive path, or <see langword="null"/> when no archive is advertised.</returns>
    /// <exception cref="ArgumentException"><paramref name="indexPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The archive path escapes the index directory.</exception>
    public static string? ResolveArchivePath(
        string indexPath,
        IContainerImageIndexEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ArchivePath is null)
        {
            return null;
        }

        if (!ContainerImageValidation.IsRelativeArchivePath(entry.ArchivePath))
        {
            throw new InvalidDataException(
                $"Image archivePath '{entry.ArchivePath}' must be a relative path.");
        }

        string documentPath = Path.GetFullPath(indexPath);
        string directory = Path.GetDirectoryName(documentPath)
            ?? throw new InvalidDataException($"Image index path '{indexPath}' has no parent directory.");
        string portablePath = ContainerImageValidation.NormalizePathSeparators(entry.ArchivePath);
        if (!ContainerImageValidation.TryResolveContainedPath(
                directory,
                portablePath,
                out string resolved))
        {
            throw new InvalidDataException(
                $"Image archivePath '{entry.ArchivePath}' escapes index directory '{directory}'.");
        }

        return resolved;
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 65536,
        useAsync: true);

    private static async Task<T> DeserializeAsync<T>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string path,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            T? document = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
                .ConfigureAwait(false);
            return document ?? throw Invalid(path, "document must contain one JSON object.");
        }
        catch (JsonException exception)
        {
            throw Invalid(path, exception.Message, exception);
        }
    }

    private static IContainerImageIndexEntry ValidateEntry(
        string resource,
        string repository,
        string digest,
        string? tag,
        string? archivePath,
        bool? aot,
        string baseImage,
        string? registry,
        string location)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw Invalid(location, "resource must be a non-empty string.");
        }

        if (!ContainerImageValidation.IsRepository(repository))
        {
            throw Invalid(
                location,
                "repository must be non-empty and contain no whitespace, URI scheme, tag suffix, query, fragment, backslash, empty segment, or '@'.");
        }

        if (!ContainerImageValidation.TryNormalizeDigest(digest, out string normalizedDigest))
        {
            throw Invalid(location, "digest must be 'sha256:' followed by 64 hexadecimal characters.");
        }

        if (tag is not null && string.IsNullOrWhiteSpace(tag))
        {
            throw Invalid(location, "tag must be omitted rather than empty.");
        }

        if (archivePath is not null
            && !ContainerImageValidation.IsRelativeArchivePath(archivePath))
        {
            throw Invalid(location, "archivePath must be omitted or be a non-empty relative path.");
        }

        if (aot is null)
        {
            throw Invalid(location, "aot is required and must be a JSON boolean.");
        }

        if (string.IsNullOrWhiteSpace(baseImage))
        {
            throw Invalid(location, "baseImage must be a non-empty string.");
        }

        if (registry is not null
            && !string.Equals(registry, LateBoundRegistry, StringComparison.Ordinal))
        {
            throw Invalid(location, "registry must be null or '<late-bound>'.");
        }

        if (string.Equals(registry, LateBoundRegistry, StringComparison.Ordinal)
            && ContainerImageValidation.HasRegistryAuthority(repository))
        {
            throw Invalid(
                location,
                "repository must exclude a registry authority when registry is '<late-bound>'.");
        }

        return new ContainerImageIndexEntry(
            resource,
            repository,
            normalizedDigest,
            tag,
            archivePath,
            aot.Value,
            baseImage,
            registry);
    }

    private static void RequireSchema(string schema, string path)
    {
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            throw Invalid(path, $"schema must equal '{Schema}', not '{schema}'.");
        }
    }

    private static InvalidDataException Invalid(
        string path,
        string message,
        Exception? innerException = null) =>
        new($"Image index '{path}' is invalid: {message}", innerException);
}
