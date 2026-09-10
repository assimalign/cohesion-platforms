# ContainerImageIndexes

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public static class ContainerImageIndexes
```

Reads and validates the exact `cohesion/images/v1` JSON contract and converts its entries into
digest-pinned gateway artifacts.

## Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `Schema` | `cohesion/images/v1` | The only supported document schema. |
| `LateBoundRegistry` | `<late-bound>` | The target must supply the registry authority. |

## Methods

| Method | Purpose |
| --- | --- |
| `ReadImageAsync(path, cancellationToken)` | Reads one strict per-resource `image.json`. |
| `ReadApplicationAsync(path, cancellationToken)` | Reads one strict `application.images.json` and rejects duplicate resources. |
| `Resolve(index, resource, artifact)` | Resolves only the owning resource's `ArtifactRef.Self` entry. |
| `CreateArtifact(resource, entry, registry)` | Applies a target registry only to a `<late-bound>` entry and creates a pinned artifact. |
| `ResolveArchivePath(indexPath, entry)` | Resolves a portable relative archive path inside the index directory. |

Malformed JSON, unknown fields, schema/identity violations, missing own entries, non-self artifact
references, and escaping archive paths throw `InvalidDataException`. Empty arguments or invalid
registry authorities throw `ArgumentException`; null reference arguments throw
`ArgumentNullException`. File I/O can throw `IOException`, and asynchronous reads honor
`CancellationToken`.

The complete wire contract is in [IMAGE_INDEX.md](../../../IMAGE_INDEX.md).

Back to the [namespace overview](../OVERVIEW.md).
