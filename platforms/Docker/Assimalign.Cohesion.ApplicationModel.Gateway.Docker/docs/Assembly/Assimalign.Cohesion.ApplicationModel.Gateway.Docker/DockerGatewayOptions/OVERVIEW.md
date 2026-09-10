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
| `ImageRealizer` | `IImageRealizer?` | `null` | Custom digest-pinned image realization. The default accepts an existing verified engine image or loads a configured OCI archive and verifies it. |
| `ImageArchives` | `IDictionary<string, string>` | Empty ordinal dictionary | Mutable mapping from a digest-pinned image reference to an OCI archive path for the default image realizer. |
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
- `PublicHost` must be non-empty and valid for endpoint URI construction.
- `WarningHandler` must not be null.
- Observation, probe, timeout, and event reconnect intervals must be greater than zero.
- The liveness threshold must be at least one; restart attempts cannot be negative; initial
  backoff cannot be negative; maximum backoff cannot be less than initial backoff.
- Every `ImageArchives` key and value must be non-empty, and each key must be a valid digest-pinned
  container image reference.

Invalid settings throw `ArgumentException`, `ArgumentNullException`,
`ArgumentOutOfRangeException`, or `NotSupportedException` as appropriate.

Back to the [namespace overview](../OVERVIEW.md).
