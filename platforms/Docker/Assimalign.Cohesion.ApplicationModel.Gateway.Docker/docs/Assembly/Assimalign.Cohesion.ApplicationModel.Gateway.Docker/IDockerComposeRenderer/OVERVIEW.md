# IDockerComposeRenderer

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`

```csharp
public interface IDockerComposeRenderer
```

Defines the Docker package's daemon-free rendering seam for one resolved Cohesion resource.

## Method

```csharp
string Render(
    ResourcePlan plan,
    IContainerImageArtifact artifact,
    ResourceInputs inputs,
    IReadOnlyList<ResourceDependencyObservation> dependencies,
    ApplicationName application,
    string owner);
```

| Parameter | Meaning |
| --- | --- |
| `plan` | Validated platform-neutral `cohesion/plan/v1` resource plan. |
| `artifact` | Digest-pinned container image artifact. |
| `inputs` | Resolved mount and bootstrap-credential inputs. |
| `dependencies` | Immutable observed dependency snapshot used for runtime-contract projection. |
| `application` | Application that owns the rendered Docker objects. |
| `owner` | Gateway ownership identity written into metadata. |

The method returns a deterministic Compose-style YAML document for the compiled service, network,
and claim volumes. It includes Cohesion inspection metadata for workload, plan hash, input paths,
and probes. Sensitive input bytes are not written into the document. Rendering performs no image
acquisition and creates no Docker Engine client.

## Exceptions

- `ArgumentNullException` when `plan`, `artifact`, `inputs`, or `dependencies` is null.
- `ArgumentException` when `owner` is empty or another supplied value is malformed.
- `InvalidDataException` when the plan schema/specification or compiled value shape is invalid.
- `InvalidOperationException` when required resolved inputs or observed dependency endpoints are
  absent or unresolved.

## Application-set integration

This interface is intentionally package-local, not the upstream application-set discovery
contract. The canonical `Assimalign.Cohesion.ApplicationModel` `10.0.1-preview.3` DLL does not
export the `IApplicationGatewayRenderer` interface present in sibling source. As a result, callers
can invoke `IDockerComposeRenderer.Render(...)` directly, but application-set
`--mode render --gateway docker` is not wired at the current package floor.

Back to the [namespace overview](../OVERVIEW.md).
