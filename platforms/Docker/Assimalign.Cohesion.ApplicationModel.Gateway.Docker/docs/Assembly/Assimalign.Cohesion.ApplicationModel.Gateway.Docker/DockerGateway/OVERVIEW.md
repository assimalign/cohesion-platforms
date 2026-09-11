# DockerGateway

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`

```csharp
public sealed class DockerGateway : ApplicationGateway, IDockerComposeRenderer
```

`DockerGateway` realizes validated Cohesion resource plans as containers, networks, named volumes,
ports, and observation lifetimes on a Docker-compatible engine. Its stable gateway name is
`"docker"`.

## Construction

```csharp
public DockerGateway();
public DockerGateway(DockerGatewayOptions options);
```

The parameterless constructor uses a new `DockerGatewayOptions` instance. The options constructor
validates Docker-specific settings immediately. A null options value throws
`ArgumentNullException`; malformed values can throw `ArgumentException`,
`ArgumentOutOfRangeException`, or `NotSupportedException` depending on the setting.

When `DockerGatewayOptions.EngineEndpoint` is absent, engine selection checks `DOCKER_HOST`, then
uses `npipe://./pipe/docker_engine` on Windows or `unix:///var/run/docker.sock` elsewhere. The
engine client is created lazily, so construction and direct rendering do not contact a daemon.

## Properties

| Property | Type | Behavior |
| --- | --- | --- |
| `Name` | `ResourceName` | Always returns `"docker"`. |

The gateway also inherits the public application-gateway lifecycle surface from
`ApplicationGateway`. Ordinary stop preserves owned engine objects; uninstall performs the
gateway's idempotent best-effort teardown.

## Image gathering

The default gather path accepts only a digest-pinned manifest artifact. If
`DockerGatewayOptions.ImageIndexPath` is configured, it resolves the resource's own
`ArtifactRef.Self` entry, checks the index application and source repository/digest against the
manifest, preserves a concrete pinned entry registry, applies `ContainerRegistry` only when the
entry registry is omitted or null, and resolves any `archive` relative to the index. The strict
index reader also requires a lowercase OCI `platform`; an unavailable archive must be omitted.
An existing engine image is reused only after its digest is proved. Otherwise the gateway verifies
and loads the archive or pulls by digest, then returns the immutable image ID used by
`DockerPlanCompiler` at container creation.

`ImageArchives` remains a compatibility path when no index is selected. A custom `ImageRealizer`
bypasses default archive and pull behavior, but cannot resolve a tag-only manifest or substitute
another repository/digest. Configured index validation and registry binding still run before it
receives the canonical reference.

## Direct rendering

```csharp
public string Render(
    ResourcePlan plan,
    IContainerImageArtifact artifact,
    ResourceInputs inputs,
    IReadOnlyList<ResourceDependencyObservation> dependencies,
    ApplicationName application,
    string owner);
```

`Render` compiles one resource through the same `DockerPlanCompiler` used for reconciliation and
returns a deterministic Compose-style YAML document. It reports compiler warnings through
`DockerGatewayOptions.WarningHandler` and never opens an Engine API connection.

Null plan, artifact, inputs, or dependency values throw `ArgumentNullException`. An empty owner
throws `ArgumentException`. Unsupported plan schema/specification throws `InvalidDataException`;
missing or unresolved inputs and dependency endpoints throw `InvalidOperationException`.

This method is the public `IDockerComposeRenderer` contract. It is not currently application-set
CLI render support: the resolved canonical ApplicationModel `10.0.1-preview.3` assembly lacks
`IApplicationGatewayRenderer`, so `--mode render --gateway docker` cannot dispatch here.

## Lifecycle policy

The compiler remains based on `ResourcePlan`. During gateway validation and reconciliation, the
runtime path separately reads the generic manifest carried by the resource control context:

- `Manifest.Lifecycle.RestartPolicy` selects `Always`, `OnFailure`, or `Never` supervision.
- `Manifest.Lifecycle.ExitCodes` must equal `cohesion/sysexits/v1`; configuration exit 64 and
  startup exit 70 are final.

Back to the [namespace overview](../OVERVIEW.md).
