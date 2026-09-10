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
| `Repository` | `string` | Lowercase repository, excluding any late-bound registry prefix. |
| `Digest` | `string` | Lowercase SHA-256 manifest digest. |
| `Tag` | `string?` | Optional display tag; never an acquisition reference. |
| `ArchivePath` | `string?` | Optional portable path relative to the index document. |
| `Aot` | `bool` | Whether the image contains a NativeAOT executable. |
| `BaseImage` | `string` | Publisher-recorded base-image identity. |
| `Registry` | `string?` | Either null or the exact `<late-bound>` marker. |

Instances are immutable internal implementations returned by the `ContainerImageIndexes` readers.

Back to the [namespace overview](../OVERVIEW.md).
