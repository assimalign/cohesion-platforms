# IOciImageStore

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public interface IOciImageStore
```

Ingests image content into a digest-addressed store. Every manifest, config, and layer required by
the selected image must be present in the input and is hashed before the repository link is
committed.

## Property

`RootPath` is the store's absolute root directory.

## Methods

```csharp
Task IngestAsync(
    IContainerImageIndexEntry image,
    string imageIndexPath,
    CancellationToken cancellationToken = default)

Task IngestAsync(
    string repository,
    string digest,
    string archivePath,
    CancellationToken cancellationToken = default)
```

The first overload resolves `image.ArchivePath` relative to its index. The second accepts an OCI
image-layout directory, OCI image-layout tarball, gzip-compressed tarball, or Docker-save tarball.
OCI input preserves and verifies its manifest digest. Docker-save input is accepted only when the
digest matches the deterministic Docker schema-2 manifest reconstructed from its config and
layers, because that archive format does not retain original registry-manifest bytes.

Missing, malformed, incomplete, corrupt, or digest-mismatched content throws
`InvalidDataException`; missing files and other storage failures surface through `IOException`.
Empty or invalid identities throw `ArgumentException`, null reference arguments throw
`ArgumentNullException`, and cancellation throws `OperationCanceledException`.

Back to the [namespace overview](../OVERVIEW.md).
