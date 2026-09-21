# ContainerImageIndexes

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public static class ContainerImageIndexes
```

Reads and validates the exact per-resource `cohesion/image/v1` and application
`cohesion/images/v1` JSON contracts and converts their entries into digest-pinned gateway
artifacts.

## Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `ImageSchema` | `cohesion/image/v1` | The required schema for per-resource `image.json`. |
| `ApplicationSchema` | `cohesion/images/v1` | The required schema for gateway-level `application.images.json`. |

## Methods

| Method | Purpose |
| --- | --- |
| `ReadImageAsync(path, cancellationToken)` | Reads one strict per-resource `image.json`. |
| `ReadApplicationAsync(path, cancellationToken)` | Reads one strict `application.images.json` and rejects duplicate resources. |
| `Resolve(index, resource, artifact)` | Resolves only the owning resource's `ArtifactRef.Self` entry. |
| `CreateArtifact(resource, entry, registry)` | Applies a target registry only when the entry registry is omitted or null; a pinned entry registry takes precedence. |
| `ResolveArchivePath(indexPath, entry)` | Resolves a portable relative archive path inside the index directory. |

Malformed JSON, unknown properties, schema/identity/platform violations, missing own entries,
non-self artifact references, a present null `archive`, and escaping archive paths throw
`InvalidDataException`. Empty arguments or invalid registry authorities throw
`ArgumentException`; null reference arguments throw `ArgumentNullException`. File I/O can throw
`IOException`, and asynchronous reads honor `CancellationToken`.

The complete wire contract is in [IMAGE_INDEX.md](../../../IMAGE_INDEX.md).

Back to the [namespace overview](../OVERVIEW.md).
