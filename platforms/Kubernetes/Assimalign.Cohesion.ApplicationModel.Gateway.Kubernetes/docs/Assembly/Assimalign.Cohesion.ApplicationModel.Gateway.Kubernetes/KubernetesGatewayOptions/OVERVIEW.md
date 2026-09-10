# KubernetesGatewayOptions

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`

```csharp
public sealed class KubernetesGatewayOptions : ApplicationGatewayOptions
```

Configures cluster selection, image acquisition, object patching, diagnostics, and teardown.
Common application-gateway options are inherited from `ApplicationGatewayOptions`.

## Properties

| Property | Type | Default | Purpose |
| --- | --- | --- | --- |
| `KubeConfigPath` | `string?` | `null` | Explicit kubeconfig path; otherwise environment, default-file, then in-cluster resolution is used. |
| `ContextName` | `string?` | `null` | Explicit kubeconfig context; otherwise its current context is used. |
| `ImageIndexPath` | `string?` | `null` | Path to the strict `application.images.json` used to resolve each resource's own image. |
| `ContainerRegistry` | `string?` | `null` | Registry authority applied only to a `<late-bound>` image-index entry. |
| `FieldManager` | `string` | `cohesion-gateway` | Fallback server-side-apply field manager for gateway bootstrap objects. |
| `ImageRealizer` | `IImageRealizer?` | `null` | Optional no-index acquisition seam; its result must preserve the manifest repository and digest. |
| `WarningHandler` | `Action<string>` | Standard error | Receives distinct compiler and Kind-availability warnings. |
| `StopGrace` | `TimeSpan` | 30 seconds | Maximum wait for owned namespace deletion during teardown. |

## Patch registration

```csharp
KubernetesGatewayOptions Patch<TResource>(
    ResourceName resource,
    Action<TResource> patch)
```

Registers a Kubernetes-native mutation for objects compiled for one resource. Patches execute in
registration order before mandatory Cohesion metadata is restored. A null patch throws
`ArgumentNullException`.

## Image acquisition

When `ImageIndexPath` is set, Gather requires the index application and the resource entry's
repository/digest to match the manifest. In Development on a `kind-<cluster>` context, an
advertised archive is verified and passed to `kind load image-archive --name <cluster>`. If Kind is
unavailable, late-bound images require `ContainerRegistry`; there is no implicit tag or registry
pull fallback. Outside Kind, a late-bound entry likewise requires this authority. Fixed
repositories are never prefixed.

`ImageIndexPath`, when supplied, must be non-empty. `ContainerRegistry` must be an authority with
no URI scheme, repository path, credentials, query, or fragment. Other validation requires
non-empty kubeconfig/context/field-manager values when supplied, a non-null warning handler, and a
positive stop grace. Invalid settings throw `ArgumentException`, `ArgumentNullException`, or
`ArgumentOutOfRangeException` as appropriate.

Back to the [namespace overview](../OVERVIEW.md).
