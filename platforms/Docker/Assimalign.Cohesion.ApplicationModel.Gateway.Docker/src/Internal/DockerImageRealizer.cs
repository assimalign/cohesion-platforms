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
            if (!TryFindArchive(canonicalReference, out string archivePath))
            {
                throw new FileNotFoundException(
                    $"Docker image '{canonicalReference}' for resource '{resource}' is not present in the engine and no OCI archive is configured for it.");
            }

            OciImageArchiveVerification verification;
            try
            {
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
                    $"Docker image archive '{archivePath}' for resource '{resource}' failed verification: {exception.Message}",
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

            image = await _engine
                .InspectImageAsync(verification.ImageId, cancellationToken)
                .ConfigureAwait(false);
            if (image is null)
            {
                throw new InvalidDataException(
                    $"Docker loaded OCI archive '{archivePath}' for resource '{resource}', but verified image ID '{verification.ImageId}' is absent.");
            }

            if (!string.Equals(image.Id, verification.ImageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Docker loaded OCI archive '{archivePath}' for resource '{resource}', but inspected image ID '{image.Id}' does not match verified archive image ID '{verification.ImageId}'.");
            }
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
        string[] digests = image.RepoDigests ?? [];
        for (int index = 0; index < digests.Length; index++)
        {
            if (string.Equals(digests[index], canonicalReference, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidDataException(
            $"Docker image inspection did not prove requested digest '{canonicalReference}'.");
    }
}
