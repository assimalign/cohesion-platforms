namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Describes one resource image from a Cohesion image index.
/// </summary>
public interface IContainerImageIndexEntry
{
    /// <summary>Gets the resource that owns the image.</summary>
    ResourceName Resource { get; }

    /// <summary>Gets the repository without a registry authority prefix.</summary>
    string Repository { get; }

    /// <summary>
    /// Gets the pinned registry authority, or <see langword="null"/> when the target supplies it.
    /// </summary>
    string? Registry { get; }

    /// <summary>Gets the optional human-readable tag, which is never used to pull.</summary>
    string? Tag { get; }

    /// <summary>Gets the immutable SHA-256 manifest digest.</summary>
    string Digest { get; }

    /// <summary>Gets the required OCI platform string, such as <c>linux/amd64</c>.</summary>
    string Platform { get; }

    /// <summary>Gets a value indicating whether the image contains a NativeAOT executable.</summary>
    bool Aot { get; }

    /// <summary>Gets the base-image identity recorded by the publisher.</summary>
    string BaseImage { get; }

    /// <summary>Gets the optional archive path, relative to the index document.</summary>
    string? Archive { get; }
}
