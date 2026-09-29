using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerImageRealizer : IImageRealizer
{
    private readonly IDockerEngineClient _engine;
    private readonly IReadOnlyDictionary<string, string> _archives;

    public DockerImageRealizer(
        IDockerEngineClient engine,
        IReadOnlyDictionary<string, string> archives)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(archives);
        _engine = engine;
        _archives = archives;
    }

    public async Task<IContainerImageArtifact> RealizeAsync(
        ResourceId resource,
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        IContainerImageArtifact expected = ContainerImageArtifacts.Create(resource, imageReference);
        string canonicalReference = $"{expected.Repository}@{expected.Digest}";
        DockerImageInspectResponse? image = await _engine
            .InspectImageAsync(canonicalReference, cancellationToken)
            .ConfigureAwait(false);

        if (image is null)
        {
            image = TryFindArchive(canonicalReference, out string archivePath)
                ? await LoadArchiveAsync(
                    expected,
                    archivePath,
                    resource,
                    cancellationToken).ConfigureAwait(false)
                : await PullAsync(
                    expected,
                    canonicalReference,
                    resource,
                    cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RequireDigest(image, canonicalReference);
        }

        if (string.IsNullOrWhiteSpace(image.Id))
        {
            throw new InvalidDataException(
                $"Docker image '{canonicalReference}' for resource '{resource}' has no immutable engine image ID.");
        }

        return new DockerImageArtifact(
            resource,
            expected.Repository,
            expected.Digest,
            expected.Tag,
            image.Id);
    }

    private async Task<DockerImageInspectResponse> PullAsync(
        IContainerImageArtifact expected,
        string canonicalReference,
        ResourceId resource,
        CancellationToken cancellationToken)
    {
        await _engine
            .PullByDigestAsync(expected.Repository, expected.Digest, cancellationToken)
            .ConfigureAwait(false);
        DockerImageInspectResponse? image = await _engine
            .InspectImageAsync(canonicalReference, cancellationToken)
            .ConfigureAwait(false);
        if (image is null)
        {
            throw new InvalidDataException(
                $"Docker pulled image '{canonicalReference}' for resource '{resource}', " +
                "but the digest-pinned reference is absent from the selected engine.");
        }

        RequireDigest(image, canonicalReference);
        return image;
    }

    private async Task<DockerImageInspectResponse> LoadArchiveAsync(
        IContainerImageArtifact expected,
        string archivePath,
        ResourceId resource,
        CancellationToken cancellationToken)
    {
        OciImageArchiveVerification verification;
        try
        {
            await VerifyArchiveClosureAsync(
                expected.Repository,
                expected.Digest,
                archivePath,
                cancellationToken).ConfigureAwait(false);
            verification = await OciImageArchiveVerifier
                .VerifyAsync(archivePath, expected.Digest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException)
        {
            throw new InvalidDataException(
                $"Docker image archive '{archivePath}' for resource '{resource}' failed " +
                $"verification: {exception.Message}",
                exception);
        }

        await using (FileStream archive = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true))
        {
            await _engine.LoadImageAsync(archive, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        DockerImageInspectResponse? image = await _engine
            .InspectImageAsync(verification.ImageId, cancellationToken)
            .ConfigureAwait(false);
        if (image is null)
        {
            throw new InvalidDataException(
                $"Docker loaded OCI archive '{archivePath}' for resource '{resource}', " +
                $"but verified image ID '{verification.ImageId}' is absent.");
        }

        if (!string.Equals(image.Id, verification.ImageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Docker loaded OCI archive '{archivePath}' for resource '{resource}', but " +
                $"inspected image ID '{image.Id}' does not match verified archive image ID " +
                $"'{verification.ImageId}'.");
        }

        return image;
    }

    private static async Task VerifyArchiveClosureAsync(
        string repository,
        string digest,
        string archivePath,
        CancellationToken cancellationToken)
    {
        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-image-{Guid.NewGuid():N}");
        try
        {
            IOciImageStore store = OciImageStores.Create(storeRoot);
            await store
                .IngestAsync(repository, digest, archivePath, cancellationToken)
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

    private bool TryFindArchive(string canonicalReference, out string archivePath)
    {
        if (_archives.TryGetValue(canonicalReference, out string? direct)
            && direct is not null)
        {
            archivePath = direct;
            return true;
        }

        foreach ((string candidate, string path) in _archives)
        {
            if (string.Equals(candidate, canonicalReference, StringComparison.OrdinalIgnoreCase))
            {
                archivePath = path;
                return true;
            }
        }

        archivePath = string.Empty;
        return false;
    }

    internal static void RequireDigest(
        DockerImageInspectResponse image,
        string canonicalReference)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalReference);
        IContainerImageArtifact expected = ContainerImageArtifacts.Create(
            default,
            canonicalReference);
        string expectedRepository = NormalizeDockerRepository(expected.Repository);
        string[] digests = image.RepoDigests ?? [];
        for (int index = 0; index < digests.Length; index++)
        {
            IContainerImageArtifact candidate;
            try
            {
                candidate = ContainerImageArtifacts.Create(default, digests[index]);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (string.Equals(
                    NormalizeDockerRepository(candidate.Repository),
                    expectedRepository,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidate.Digest,
                    expected.Digest,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidDataException(
            $"Docker image inspection did not prove requested digest '{canonicalReference}'.");
    }

    private static string NormalizeDockerRepository(string repository)
    {
        int separator = repository.IndexOf('/');
        if (separator < 0)
        {
            return $"docker.io/library/{repository}";
        }

        string first = repository[..separator];
        bool hasRegistry = string.Equals(first, "localhost", StringComparison.OrdinalIgnoreCase)
            || first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal);
        if (!hasRegistry)
        {
            return $"docker.io/{repository}";
        }

        string registry = string.Equals(first, "index.docker.io", StringComparison.OrdinalIgnoreCase)
            ? "docker.io"
            : first;
        string path = repository[(separator + 1)..];
        if (string.Equals(registry, "docker.io", StringComparison.OrdinalIgnoreCase)
            && !path.Contains('/', StringComparison.Ordinal))
        {
            path = $"library/{path}";
        }

        return $"{registry}/{path}";
    }
}
