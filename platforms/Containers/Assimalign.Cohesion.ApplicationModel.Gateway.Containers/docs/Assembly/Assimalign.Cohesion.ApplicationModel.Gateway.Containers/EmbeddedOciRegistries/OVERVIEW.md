# EmbeddedOciRegistries

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public static class EmbeddedOciRegistries
```

Creates a pull-only OCI Distribution Registry HTTP API v2 listener backed by an existing image
store.

## Factory

```csharp
IEmbeddedOciRegistry Create(string storePath, int port = 0)
```

`port` may be zero for an ephemeral loopback port or an explicit value from 1 through 65535. The
returned registry is stopped until `StartAsync` is called. An empty or null store path throws
`ArgumentException` or `ArgumentNullException`; an invalid port throws
`ArgumentOutOfRangeException`.

Back to the [namespace overview](../OVERVIEW.md).
