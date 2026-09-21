using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Creates immutable, digest-pinned container image artifacts for platform gateways.
/// </summary>
public static class ContainerImageArtifacts
{
    /// <summary>
    /// Creates an artifact from a pull reference in
    /// <c>{repository}@sha256:{digest}</c> form.
    /// </summary>
    /// <param name="resource">The resource that consumes the image.</param>
    /// <param name="imageReference">The digest-pinned image pull reference.</param>
    /// <param name="tag">An optional human-readable tag. It is never used for pulling.</param>
    /// <returns>The validated immutable image artifact.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imageReference"/> is empty or is not a digest-pinned SHA-256 reference, or
    /// <paramref name="tag"/> is explicitly empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="imageReference"/> is <see langword="null"/>.</exception>
    public static IContainerImageArtifact Create(
        ResourceId resource,
        string imageReference,
        string? tag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        int separator = imageReference.LastIndexOf('@');
        if (separator <= 0 || separator == imageReference.Length - 1)
        {
            throw new ArgumentException(
                "A container image reference must use {repository}@sha256:{digest} form.",
                nameof(imageReference));
        }

        string repository = imageReference[..separator];
        if (!ContainerImageValidation.IsRepository(repository))
        {
            throw new ArgumentException(
                "A container image repository must be non-empty and contain no tag suffix, URI syntax, whitespace, empty segment, or '@'.",
                nameof(imageReference));
        }

        string digest = imageReference[(separator + 1)..];
        if (!ContainerImageValidation.TryNormalizeDigest(digest, out string normalizedDigest))
        {
            throw new ArgumentException(
                "A container image digest must be 'sha256:' followed by 64 hexadecimal characters.",
                nameof(imageReference));
        }

        if (tag is not null && string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException(
                "A container image tag must be omitted rather than empty.",
                nameof(tag));
        }

        return new ContainerImageArtifact(resource, repository, normalizedDigest, tag);
    }
}
