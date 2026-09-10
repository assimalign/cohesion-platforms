# ContainerImageArtifacts

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public static class ContainerImageArtifacts
```

Creates immutable `IContainerImageArtifact` values from a digest-pinned reference.

## Factory

```csharp
IContainerImageArtifact Create(
    ResourceId resource,
    string imageReference,
    string? tag = null)
```

`imageReference` must use `{repository}@sha256:{64 hexadecimal characters}` form. The lowercase
repository cannot carry a tag, URI scheme, query, fragment, whitespace, empty segment, backslash,
or second `@`. Hexadecimal digest characters are normalized to lowercase. `tag` is display metadata only
and is never used for acquisition; when supplied, it must not be empty.

An invalid value throws `ArgumentException`; a null reference throws `ArgumentNullException`.

Back to the [namespace overview](../OVERVIEW.md).
