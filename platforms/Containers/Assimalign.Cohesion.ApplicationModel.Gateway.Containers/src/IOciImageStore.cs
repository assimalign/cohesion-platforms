using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Ingests container image layouts and archives into a digest-verified on-disk content store.
/// </summary>
public interface IOciImageStore
{
    /// <summary>Gets the absolute root directory of the content-addressed store.</summary>
    string RootPath { get; }

    /// <summary>
    /// Ingests the archive advertised by a validated image-index entry.
    /// </summary>
    /// <param name="image">The image entry whose digest and repository are authoritative.</param>
    /// <param name="imageIndexPath">The index path against which <c>archivePath</c> is resolved.</param>
    /// <param name="cancellationToken">Signals that ingestion should stop.</param>
    /// <returns>A task that completes after every referenced blob has been verified and stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="imageIndexPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="imageIndexPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The entry has no archive or its content does not match its digest.</exception>
    /// <exception cref="IOException">The archive or store cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task IngestAsync(
        IContainerImageIndexEntry image,
        string imageIndexPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests an OCI image-layout directory, OCI image-layout tarball, or Docker-save tarball.
    /// </summary>
    /// <param name="repository">The repository under which the verified manifest is served.</param>
    /// <param name="digest">The expected SHA-256 image-manifest digest.</param>
    /// <param name="archivePath">The image-layout directory or archive path.</param>
    /// <param name="cancellationToken">Signals that ingestion should stop.</param>
    /// <returns>A task that completes after every referenced blob has been verified and stored.</returns>
    /// <exception cref="ArgumentException">A parameter is empty or is not a valid digest-pinned identity.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="repository"/>, <paramref name="digest"/>, or
    /// <paramref name="archivePath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">The image content does not match <paramref name="digest"/>.</exception>
    /// <exception cref="IOException">The archive or store cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task IngestAsync(
        string repository,
        string digest,
        string archivePath,
        CancellationToken cancellationToken = default);
}
