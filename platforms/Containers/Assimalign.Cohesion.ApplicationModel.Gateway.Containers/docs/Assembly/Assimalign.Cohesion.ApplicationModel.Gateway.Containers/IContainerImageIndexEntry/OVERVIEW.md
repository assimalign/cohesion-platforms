# IContainerImageIndexEntry

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public interface IContainerImageIndexEntry
```

Represents the validated image metadata owned by one resource.

## Properties

| Property | Type | Meaning |
| --- | --- | --- |
| `Resource` | `ResourceName` | Resource that owns the image. |
| `Repository` | `string` | Lowercase, authority-free OCI repository. |
| `Registry` | `string?` | Pinned registry authority, or null for target late binding. |
| `Tag` | `string?` | Optional display tag; never an acquisition reference. |
| `Digest` | `string` | Lowercase SHA-256 manifest digest. |
| `Platform` | `string` | Required lowercase OCI `os/architecture[/variant]` platform. |
| `Aot` | `bool` | Whether the image contains a NativeAOT executable. |
| `BaseImage` | `string` | Publisher-recorded base-image identity. |
| `Archive` | `string?` | Optional portable path relative to the index document; null means the field was omitted. |

Instances are immutable internal implementations returned by the `ContainerImageIndexes` readers.

Back to the [namespace overview](../OVERVIEW.md).
