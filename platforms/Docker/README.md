# Docker (platform area)

The Docker implementation of Cohesion's application-model gateway. Exactly one pure
`DockerPlanCompiler` translates each validated `cohesion/plan/v1` `ResourcePlan` into Docker
operations, one level-triggered `DockerPlanController` applies them idempotently, and one
events-plus-inspect observer publishes locally realized state. Podman's Docker-compatible API is
the supported local engine.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Docker` | `DockerGateway : ApplicationGateway` (`Name = "docker"`), `DockerPlanCompiler`, plan controller, BCL Engine API client, observer/supervisor, daemon-free renderer, and `UseDockerGateway()`. Design item 36 is tracked by [`L04.01.04` / #23](https://github.com/assimalign/cohesion-platforms/issues/23). |

## Design item 36 delivery

- `DockerPlanCompiler` accepts every validated v1 plan through one generic, plan-only path. It never dispatches
  on manifest kind, resource area, or CLR type. Each resource becomes one container; a
  `DaemonSet` also means one container in the single-engine topology, while a `Job` is a run-once
  container. Replica shapes Docker cannot preserve are rejected rather than silently collapsed.
- Compilation creates one application-scoped network, stable aliases for dependency discovery,
  named volumes for claims, stopped-container staging for Configuration files, and tmpfs
  declarations plus archive entries for Secret mounts and the rotating bootstrap credential.
  Public endpoints receive host port bindings; every endpoint used by a gateway-side probe also
  receives a loopback-only `127.0.0.1:0` binding, including private endpoints. The live-content
  limitation of the sensitive archive path is recorded below.
- Ownership and reconciliation metadata live on engine objects. `cohesion.io/plan-hash` is derived
  only from the immutable plan; artifacts, resolved inputs, credentials, and observed dependencies
  do not masquerade as plan drift. Unknown plan specification or schema fails before engine
  contact; unknown Docker hints warn once per gateway session and are otherwise ignored.
- `DockerPlanController` converges the application network, named volumes, and container, replaces
  stale plan-owned containers when required, and deletes in idempotent best-effort reverse order.
  Registered domain controllers are consulted before this built-in plan controller.
- The minimal Docker Engine API client uses BCL HTTP primitives for `http`, `https`, `unix`, and
  `npipe` endpoints and source-generated `System.Text.Json`. It calls unversioned `GET /version`,
  intersects the engine's `MinAPIVersion`–`ApiVersion` range with the client's supported
  v1.25–v1.51 range, selects the highest mutual version, and caches that `/vX.Y` prefix for Engine
  operations. Malformed version data or no overlap is rejected before a versioned request. This
  deliberately revises the older preference for Cohesion's connection stack: the canonical
  Cohesion `Connections.NamedPipes` package is not available in the package set consumed here, and
  the engine client must support Unix sockets and Windows named pipes through one shippable
  AOT-safe surface.
- A single observer combines the engine event stream with periodic full inspect. It is the sole
  writer of local Docker lifecycle and endpoint observations through the public
  `InMemoryResourceStateManager`. Readiness, startup, and liveness probes run in the gateway over
  the loopback bindings. The first liveness failure on a running resource publishes non-gating
  `Degraded`; three consecutive failures request a supervised restart, as required by the
  authoritative runtime design section 5. `DockerPlanController` reads
  `IManifestResource.Manifest.Lifecycle.RestartPolicy` from the resource on its control context,
  and gateway validation requires the `cohesion/sysexits/v1` exit-code contract before reconcile.
  The observer therefore honors `Always`, `OnFailure`, and `Never`, with configuration/startup
  exits final; none of this changes the compiler's plan-only boundary. Dependents are gated only
  on initial readiness.
- The public `IDockerComposeRenderer.Render(...)` API produces a deterministic Compose-style
  representation from the compiled plan and resolved inputs without opening an engine connection.
  Compose is an inspection/output shape, not the desired-state source or a second reconciliation
  path. Application-set `--mode render` is not wired against the currently pinned upstream
  package, as recorded below.
- The default image path accepts an explicit digest-pinned image-reference-to-OCI-archive mapping,
  verifies the indexed manifest and config blobs, derives and inspects the immutable engine image
  ID, and then runs by that ID even when Docker did not assign the loaded image a
  `repository@digest` alias. This is the smallest bridge available until design item 35 supplies
  the shared image index and acquisition machinery; callers may instead provide an
  `IImageRealizer`.
- The NuGet package contributes the `docker` `CohesionGatewayProvider` through `buildTransitive`
  metadata with `RequiresJit=false`.

## Layering and AOT posture

- Depends on the generic ApplicationModel contract and Gateway base packages plus
  `platforms/Containers`. It never references a resource area's `.ApplicationModel`, `*.Hosting`,
  `.Application` runtime, or a `Microsoft.Extensions.*` assembly.
- This project explicitly sets `<IsAotCompatible>true</IsAotCompatible>`. That is a scoped item 36
  exception to this repository's default of carrying no AOT mandate; it does not restore a
  repository-wide rule. The BCL transport, source-generated JSON, and static provider metadata are
  kept trim/AOT safe.
- The current upstream `Sdk.Gateway` early auto-AOT allowlist recognizes only Local and InProcess,
  not Docker. `RequiresJit=false` is correct provider metadata, but automatic Docker NativeAOT
  selection remains an upstream SDK gap; a consumer can opt in explicitly meanwhile.

## Lifecycle and compatibility boundaries

- `StopAsync` stops each running container with its plan grace budget (the default plan value is
  30 seconds), then releases supervision and observation while retaining the application network,
  named volumes, and other persistent engine state. Stop is not teardown.
- `UninstallAsync` (`--mode teardown`) removes reached containers, selected owned volumes, and the
  application network in best-effort reverse order. Missing objects are success, every later
  deletion is attempted after an earlier failure, and retrying teardown is safe. The upstream base
  cannot report custom-controller delete outcomes; therefore a shared application network cannot
  be proven safe to delete for custom-controller or mixed-controller models and is retained
  conservatively pending that seam.
- `ResourcePlan` v1 does not carry the default control-plane endpoint/path or a private endpoint's
  URI scheme. Docker can compile explicit plan probes, but it cannot faithfully recover those
  omitted values. Restart policy is different: it is deliberately read from the generic manifest
  on `IResourceControlContext.Resource` by the controller path, while `DockerPlanCompiler` remains
  purely `ResourcePlan`-based.
- Sensitive-input content is not yet reliably materialized into the live tmpfs mounts. The
  controller stages ordinary Configuration files while the container is stopped, emits
  `HostConfig.Tmpfs` for sensitive paths, starts the container, and then calls
  `PUT /containers/{id}/archive` for Secret/bootstrap files. Moby's `openContainerFS` performs
  archive extraction in a filesystem view whose tmpfs mounts are private, so writes below those
  mounts are not visible to the container's processes; starting first also prevents an atomic PID
  1 bootstrap. A portable implementation needs an image-cooperative helper/entrypoint that
  populates the live mount, or an upstream engine/gateway seam that can stage it before PID 1 runs.
  The current implementation must not be treated as completed live Secret/bootstrap delivery.
- The resolved canonical `Assimalign.Cohesion.ApplicationModel` `10.0.1-preview.3` DLL does not
  export `IApplicationGatewayRenderer`, although the sibling cohesion source contains that
  interface and application-set render dispatch. Docker therefore cannot implement the upstream
  contract against the pinned package: direct `IDockerComposeRenderer.Render(...)` works, but
  application-set `--mode render --gateway docker` cannot discover or invoke it until a new
  immutable upstream package carries the interface and this repository advances its package floor.
- The daemon smoke test is cleanly skipped when no compatible Docker/Podman endpoint is available.
  Real daemon end-to-end coverage remains `L04.01.04.05`; item 36 does not claim it.

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing. Project-level
details are in the [overview](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/OVERVIEW.md),
[design](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/DESIGN.md), and
[assembly reference](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/Assembly/Assimalign.Cohesion.ApplicationModel.Gateway.Docker/OVERVIEW.md).
