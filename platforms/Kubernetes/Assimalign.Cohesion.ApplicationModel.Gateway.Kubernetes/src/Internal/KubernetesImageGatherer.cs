using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesImageGatherer
{
    private readonly KubernetesGatewayOptions _options;
    private readonly IKindImageLoader _kindImages;
    private readonly IKubernetesImageArchiveVerifier _archives;

    public KubernetesImageGatherer(
        KubernetesGatewayOptions options,
        IKindImageLoader kindImages)
        : this(options, kindImages, new KubernetesImageArchiveVerifier())
    {
    }

    internal KubernetesImageGatherer(
        KubernetesGatewayOptions options,
        IKindImageLoader kindImages,
        IKubernetesImageArchiveVerifier archives)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kindImages);
        ArgumentNullException.ThrowIfNull(archives);
        _options = options;
        _kindImages = kindImages;
        _archives = archives;
    }

    public async Task<IContainerImageArtifact> GatherAsync(
        IApplicationResource resource,
        ArtifactRef artifactReference,
        bool isDevelopment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot be realized by the Kubernetes gateway because " +
                $"it does not expose an {nameof(IManifestResource)} manifest.");
        }

        string? image = manifestResource.Manifest.Artifact.Image;
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot be realized by the Kubernetes gateway because " +
                "its manifest does not declare artifact.image.");
        }

        if (_options.ImageIndexPath is null)
        {
            return await GatherWithoutIndexAsync(resource, image, cancellationToken)
                .ConfigureAwait(false);
        }

        IContainerImageArtifact expected = ContainerImageArtifacts.Create(resource.Id, image);
        IApplicationImageIndex index = await ReadIndexAsync(
            _options.ImageIndexPath,
            cancellationToken).ConfigureAwait(false);
        if (index.Application != manifestResource.Manifest.Application)
        {
            throw new InvalidDataException(
                $"Application image index '{_options.ImageIndexPath}' belongs to application " +
                $"'{index.Application}', not manifest application '{manifestResource.Manifest.Application}' " +
                $"for resource '{resource.Name}'.");
        }

        IContainerImageIndexEntry entry = ContainerImageIndexes.Resolve(
            index,
            resource.Name,
            artifactReference);
        IContainerImageArtifact indexed = ContainerImageIndexes.CreateArtifact(resource.Id, entry);
        if (!string.Equals(indexed.Repository, expected.Repository, StringComparison.Ordinal)
            || !string.Equals(indexed.Digest, expected.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application image index '{_options.ImageIndexPath}' entry for resource " +
                $"'{resource.Name}' resolves to '{indexed.Repository}@{indexed.Digest}', but the " +
                $"resource manifest declares '{expected.Repository}@{expected.Digest}'.");
        }

        string? archivePath = ContainerImageIndexes.ResolveArchivePath(
            _options.ImageIndexPath,
            entry);
        if (archivePath is not null && !File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                $"Image archive '{archivePath}' advertised for resource '{resource.Name}' by " +
                $"application image index '{_options.ImageIndexPath}' does not exist.",
                archivePath);
        }

        if (archivePath is not null)
        {
            await _archives
                .VerifyAsync(entry, _options.ImageIndexPath, cancellationToken)
                .ConfigureAwait(false);
        }

        KindImageLoadResult kindLoad = KindImageLoadResult.NotKind;
        if (isDevelopment && archivePath is not null)
        {
            kindLoad = await _kindImages
                .LoadIfKindAsync(archivePath, indexed.Digest, cancellationToken)
                .ConfigureAwait(false);
        }

        bool isLateBound = string.Equals(
            entry.Registry,
            ContainerImageIndexes.LateBoundRegistry,
            StringComparison.Ordinal);
        bool usesKindRoute = kindLoad == KindImageLoadResult.Loaded;
        if (isLateBound
            && _options.ContainerRegistry is null
            && !usesKindRoute)
        {
            throw new InvalidOperationException(
                $"Image index entry for resource '{resource.Name}' has a late-bound registry, " +
                $"but {nameof(KubernetesGatewayOptions)}.{nameof(KubernetesGatewayOptions.ContainerRegistry)} " +
                "is not configured and the image was not acquired through a Development Kind archive path.");
        }

        string? registry = isLateBound && !usesKindRoute
            ? _options.ContainerRegistry
            : null;
        return ContainerImageIndexes.CreateArtifact(resource.Id, entry, registry);
    }

    private async Task<IContainerImageArtifact> GatherWithoutIndexAsync(
        IApplicationResource resource,
        string image,
        CancellationToken cancellationToken)
    {
        IContainerImageArtifact expected = ContainerImageArtifacts.Create(resource.Id, image);
        if (_options.ImageRealizer is null)
        {
            return expected;
        }

        IContainerImageArtifact? realized = await _options.ImageRealizer
            .RealizeAsync(resource.Id, image, cancellationToken)
            .ConfigureAwait(false);
        if (realized is null)
        {
            throw new InvalidOperationException(
                $"The image realizer returned no artifact for resource '{resource.Name}'.");
        }

        if (realized.Resource != resource.Id)
        {
            throw new InvalidOperationException(
                $"The image realizer returned an artifact for resource '{realized.Resource}' " +
                $"while realizing '{resource.Id}'.");
        }

        IContainerImageArtifact validated = ContainerImageArtifacts.Create(
            resource.Id,
            $"{realized.Repository}@{realized.Digest}",
            realized.Tag);
        if (!string.Equals(validated.Repository, expected.Repository, StringComparison.Ordinal)
            || !string.Equals(validated.Digest, expected.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The image realizer returned '{validated.Repository}@{validated.Digest}' for " +
                $"resource '{resource.Name}', not manifest artifact " +
                $"'{expected.Repository}@{expected.Digest}'.");
        }

        return validated;
    }

    private static Task<IApplicationImageIndex> ReadIndexAsync(
        string indexPath,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(indexPath);
        return ContainerImageIndexes.ReadApplicationAsync(fullPath, cancellationToken);
    }
}

internal interface IKubernetesImageArchiveVerifier
{
    Task VerifyAsync(
        IContainerImageIndexEntry entry,
        string imageIndexPath,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesImageArchiveVerifier : IKubernetesImageArchiveVerifier
{
    public async Task VerifyAsync(
        IContainerImageIndexEntry entry,
        string imageIndexPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageIndexPath);

        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            "cohesion-kubernetes-image-verification",
            Guid.NewGuid().ToString("N"));
        try
        {
            IOciImageStore store = OciImageStores.Create(storeRoot);
            await store
                .IngestAsync(entry, imageIndexPath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(storeRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
