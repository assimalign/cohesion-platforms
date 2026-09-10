# DockerGatewayOptions

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`

```csharp
public sealed class DockerGatewayOptions : ApplicationGatewayOptions
```

Configures Docker Engine access, image realization, endpoint publication, observation, probes, and
supervised restarts. Common export, input-resolution, trust/control-plane, controller, credential-
lifetime, and readiness settings are inherited from `ApplicationGatewayOptions`.

## Properties

| Property | Type | Default | Purpose |
| --- | --- | --- | --- |
| `EngineEndpoint` | `Uri?` | `null` | Explicit `http`, `https`, `unix`, or `npipe` Engine API endpoint. When absent, the gateway checks `DOCKER_HOST` and then the operating-system socket default. |
| `ImageRealizer` | `IImageRealizer?` | `null` | Custom engine image acquisition. When supplied, default archive/pull acquisition is bypassed, but the manifest must remain digest-pinned and the result must preserve its identity; a configured index is also validated and registry-bound before realization. |
| `ImageIndexPath` | `string?` | `null` | Path to the shared `application.images.json` document. The default realizer resolves each resource's `ArtifactRef.Self` entry from this index. |
| `ContainerRegistry` | `string?` | `null` | Registry authority applied only to an index entry whose registry is `<late-bound>`; it contains no URI scheme or path. |
| `ImageArchives` | `IDictionary<string, string>` | Empty ordinal dictionary | Compatibility mapping from a digest-pinned image reference to an OCI archive path. Used only by the default realizer when `ImageIndexPath` is absent. |
| `PublicHost` | `string` | `"localhost"` | Host used in observed public endpoint URIs. |
| `WarningHandler` | `Action<string>` | `Console.Error.WriteLine` | Receives compiler warnings; unknown Docker hint keys are reported once per gateway session. |
| `ObservationInterval` | `TimeSpan` | 2 seconds | Full container-inspection interval. |
| `ProbeInterval` | `TimeSpan` | 1 second | Delay between unsuccessful gateway probe attempts. |
| `ProbeTimeout` | `TimeSpan` | 5 seconds | Maximum duration of one probe attempt. |
| `LivenessFailureThreshold` | `int` | 3 | Consecutive liveness failures before restart is requested; the first failure is still observed as `Degraded`. |
| `InitialRestartBackoff` | `TimeSpan` | 1 second | First supervised restart delay. |
| `MaximumRestartBackoff` | `TimeSpan` | 30 seconds | Upper bound for supervised restart delay. |
| `MaximumRestartAttempts` | `int` | 5 | Maximum supervised restart attempts before the resource is observed as failed. |
| `EventReconnectDelay` | `TimeSpan` | 1 second | Delay before reconnecting an interrupted Docker event stream. |

## Validation

`DockerGateway` validates these settings when constructed:

- `EngineEndpoint`, when supplied, must be absolute and use a supported scheme.
- `ImageIndexPath`, when supplied, must not be empty.
- `ContainerRegistry`, when supplied, must be a registry authority without a URI scheme, path,
  query, fragment, or user information.
- `PublicHost` must be non-empty and valid for endpoint URI construction.
- `WarningHandler` must not be null.
- Observation, probe, timeout, and event reconnect intervals must be greater than zero.
- The liveness threshold must be at least one; restart attempts cannot be negative; initial
  backoff cannot be negative; maximum backoff cannot be less than initial backoff.
- Every `ImageArchives` key and value must be non-empty, and each key must be a valid digest-pinned
  container image reference.

Invalid settings throw `ArgumentException`, `ArgumentNullException`,
`ArgumentOutOfRangeException`, or `NotSupportedException` as appropriate.

## Default image acquisition

With `ImageIndexPath`, Gather requires the index application to equal the resource manifest's
application, resolves that resource's own entry, and verifies that its source repository/digest
equal `Manifest.Artifact.Image`. A `<late-bound>` registry is then replaced by
`ContainerRegistry`; without that binding, the entry must advertise an `archivePath`. Relative
archive paths are resolved against the index file. `ImageArchives` is not mixed with indexed data.

The default realizer uses this order: an existing digest-proven engine image, a configured/indexed
archive, then an Engine API digest pull. Archive content is verified before load and the resulting
container runs by image ID. Pull completion is followed by an inspect whose `RepoDigests` must
contain the canonical repository/digest. `ContainerRegistry` does not carry credentials and the
current pull call sends no `X-Registry-Auth` payload.

Back to the [namespace overview](../OVERVIEW.md).
