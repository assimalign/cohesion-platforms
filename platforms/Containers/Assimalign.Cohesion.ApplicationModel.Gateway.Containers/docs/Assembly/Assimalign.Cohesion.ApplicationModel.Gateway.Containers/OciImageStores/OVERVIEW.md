# OciImageStores

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public static class OciImageStores
```

Creates or opens an `IOciImageStore` rooted at an absolute or relative directory path.

## Factory

```csharp
IOciImageStore Create(string rootPath)
```

The implementation creates store directories lazily on first ingestion. An empty path throws
`ArgumentException`; a null path throws `ArgumentNullException`.

Back to the [namespace overview](../OVERVIEW.md).
