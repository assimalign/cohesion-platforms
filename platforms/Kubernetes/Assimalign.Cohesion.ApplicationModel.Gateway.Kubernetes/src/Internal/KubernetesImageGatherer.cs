using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesImageGatherer
{
    private readonly KubernetesGatewayOptions _options;
    private readonly IKubernetesImageRegistryRoute _registry;

    public KubernetesImageGatherer(KubernetesGatewayOptions options, IKubernetesImageRegistryRoute? registry = null)
    {
        _options = options;
        _registry = registry ?? new KubernetesImageRegistryRoute(options);
    }

    public async Task<IContainerImageArtifact> GatherAsync(
        IApplicationResource resource,
        ArtifactRef artifactReference,
        bool isLocal,
        CancellationToken cancellationToken,
        string? imageIndexPath = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot be realized by the Kubernetes gateway because " +
                $"it does not expose an {nameof(IManifestResource)} manifest.");
        }

        string? image = manifestResource.Manifest.Artifact.Image;
        imageIndexPath ??= _options.ImageIndexPath;
        if (imageIndexPath is null)
        {
            if (image is null)
            {
                throw new InvalidOperationException($"Resource '{resource.Name}' needs an ImageIndexPath or Local image publication to resolve ArtifactRef.Self.");
            }

            return await GatherWithoutIndexAsync(resource, image, cancellationToken).ConfigureAwait(false);
        }
        IContainerImageArtifact? expected = image is null ? null : ContainerImageArtifacts.Create(resource.Id, image);
        IApplicationImageIndex index = await ReadIndexAsync(imageIndexPath, cancellationToken).ConfigureAwait(false);
        if (index.Application != manifestResource.Manifest.Application)
        {
            throw new InvalidDataException(
                $"Application image index '{imageIndexPath}' belongs to application " +
                $"'{index.Application}', not manifest application '{manifestResource.Manifest.Application}' " +
                $"for resource '{resource.Name}'.");
        }

        IContainerImageIndexEntry entry = ContainerImageIndexes.Resolve(
            index,
            resource.Name,
            artifactReference);
        if (expected is not null && (!string.Equals(entry.Repository, expected.Repository, StringComparison.Ordinal)
            || !string.Equals(entry.Digest, expected.Digest, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Application image index '{imageIndexPath}' entry for resource " +
                $"'{resource.Name}' declares '{entry.Repository}@{entry.Digest}', but the " +
                $"resource manifest declares '{expected.Repository}@{expected.Digest}'.");
        }

        string? archivePath = ContainerImageIndexes.ResolveArchivePath(
            imageIndexPath,
            entry);
        if (archivePath is not null && !File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                $"Image archive '{archivePath}' advertised for resource '{resource.Name}' by " +
                $"application image index '{imageIndexPath}' does not exist.",
                archivePath);
        }

        string? registry = entry.Registry ?? _options.ContainerRegistry;
        if (isLocal && await _registry.IsKindAsync(cancellationToken).ConfigureAwait(false))
        {
            registry ??= "localhost:5001";
            if (archivePath is not null && entry.Registry is null)
            {
                await _registry.PushAsync(entry, imageIndexPath, registry, cancellationToken).ConfigureAwait(false);
            }
            else if (archivePath is not null)
            {
                await new KubernetesImageArchiveVerifier().VerifyAsync(entry, imageIndexPath, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (archivePath is not null)
        {
            await new KubernetesImageArchiveVerifier().VerifyAsync(entry, imageIndexPath, cancellationToken).ConfigureAwait(false);
        }
        if (registry is null)
        {
            throw new InvalidOperationException($"Image index entry for resource '{resource.Name}' has a late-bound registry; configure KubernetesGatewayOptions.ContainerRegistry or provision a Local Kind registry.");
        }

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
