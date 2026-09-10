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
    /// <paramref name="imageReference"/> is empty or is not a digest-pinned SHA-256 reference.
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
        if (repository.IndexOf('@') >= 0
            || ContainsWhitespace(repository))
        {
            throw new ArgumentException(
                "A container image repository must be non-empty and contain no whitespace or '@'.",
                nameof(imageReference));
        }

        string digest = imageReference[(separator + 1)..];
        ValidateDigest(digest, nameof(imageReference));

        return new ContainerImageArtifact(resource, repository, digest.ToLowerInvariant(), tag);
    }

    private static bool ContainsWhitespace(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.Ordinal) ||
            digest.Length != prefix.Length + 64)
        {
            throw new ArgumentException(
                "A container image digest must be 'sha256:' followed by 64 hexadecimal characters.",
                parameterName);
        }

        for (int index = prefix.Length; index < digest.Length; index++)
        {
            char character = digest[index];
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                throw new ArgumentException(
                    "A container image digest must be 'sha256:' followed by 64 hexadecimal characters.",
                    parameterName);
            }
        }
    }
}
