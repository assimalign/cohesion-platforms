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
| `ContainerRegistry` | `string?` | `null` | Registry authority applied only when an image-index entry's `registry` is omitted or null; a pinned entry registry takes precedence. |
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
authority-free repository/digest to match the manifest and validates its required lowercase OCI
`platform`. Optional `archive` is relative and must be omitted when unavailable. In Development
on a `kind-<cluster>` context, an advertised archive is verified and passed to
`kind load image-archive --name <cluster>`. If Kind is unavailable, an entry whose `registry` is
omitted or null requires `ContainerRegistry`; there is no implicit tag or registry pull fallback.
Outside Kind, that late-bound entry likewise requires this authority. A concrete entry registry is
pinned and is never replaced by `ContainerRegistry`.

`ImageIndexPath`, when supplied, must be non-empty. `ContainerRegistry` must be an authority with
no URI scheme, repository path, credentials, query, or fragment. Other validation requires
non-empty kubeconfig/context/field-manager values when supplied, a non-null warning handler, and a
positive stop grace. Invalid settings throw `ArgumentException`, `ArgumentNullException`, or
`ArgumentOutOfRangeException` as appropriate.

Back to the [namespace overview](../OVERVIEW.md).

## System installation options

| Option | Meaning/default |
| --- | --- |
| SystemNamespace | DNS label, cohesion-system |
| SystemServiceAccount | DNS label, cohesion-gateway |
| SystemImage | Explicit digest-pinned executable image, required for bootstrap/render |
| SystemStorageSize | Explicit persistent export/state capacity, required for installation |
| SystemStorageClass | Optional PVC storage class |
| SystemExposure | None, LoadBalancer, or Ingress; default None |
| SystemIngressHost / SystemIngressClass | Both required for Ingress |
| BootstrapApply | True applies after emission; false is offline emission |

The base TrustKeyRepository option defaults to native Kubernetes Secret persistence and preserves
explicit overrides. FieldManager owns system objects; an application sharing SystemNamespace owns
the Namespace itself. CLI/deployment environment transport is described on KubernetesGatewayCommandLine.
System state and trust storage are separate from application resource teardown.