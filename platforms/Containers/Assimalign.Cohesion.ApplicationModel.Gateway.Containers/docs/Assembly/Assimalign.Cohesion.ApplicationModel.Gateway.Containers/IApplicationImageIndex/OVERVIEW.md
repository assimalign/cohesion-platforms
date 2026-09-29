# IApplicationImageIndex

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public interface IApplicationImageIndex
```

Represents a validated `application.images.json` document.

## Properties

| Property | Type | Meaning |
| --- | --- | --- |
| `Application` | `ApplicationName` | Application that owns the index. |
| `Images` | `IReadOnlyList<IContainerImageIndexEntry>` | Resource entries in declaration order. |

Instances are immutable internal implementations returned by
`ContainerImageIndexes.ReadApplicationAsync`.

Back to the [namespace overview](../OVERVIEW.md).
