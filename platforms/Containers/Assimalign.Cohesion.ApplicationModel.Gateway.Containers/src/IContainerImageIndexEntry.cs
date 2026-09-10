namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Describes one resource image from a <c>cohesion/images/v1</c> image index.
/// </summary>
public interface IContainerImageIndexEntry
{
    /// <summary>Gets the resource that owns the image.</summary>
    ResourceName Resource { get; }

    /// <summary>Gets the repository without a late-bound registry prefix.</summary>
    string Repository { get; }

    /// <summary>Gets the immutable SHA-256 manifest digest.</summary>
    string Digest { get; }

    /// <summary>Gets the optional human-readable tag, which is never used to pull.</summary>
    string? Tag { get; }

    /// <summary>Gets the optional archive path, relative to the index document.</summary>
    string? ArchivePath { get; }

    /// <summary>Gets a value indicating whether the image contains a NativeAOT executable.</summary>
    bool Aot { get; }

    /// <summary>Gets the base-image identity recorded by the publisher.</summary>
    string BaseImage { get; }

    /// <summary>
    /// Gets the registry binding marker. Version 1 permits only <see langword="null"/> or
    /// <c>&lt;late-bound&gt;</c>.
    /// </summary>
    string? Registry { get; }
}
