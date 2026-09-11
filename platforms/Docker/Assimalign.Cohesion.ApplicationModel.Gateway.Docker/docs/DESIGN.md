# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Design

> Implementation record for developer-experience image item 35 (cohesion-platforms#32) and Docker
> item 36 (`L04.01.04`, cohesion-platforms#23). The authority is
> `docs/DEVELOPER_EXPERIENCE_DESIGN.md` (signed 2026-09-06), especially runtime-contract section 5,
> and `docs/REALIZATION_PLAN.md` in the cohesion repo.

## Design intent

`DockerGateway` derives from the guided `ApplicationGateway` base and supplies Docker platform
hooks. Exactly one pure `DockerPlanCompiler` translates a validated `cohesion/plan/v1`
`ResourcePlan` into engine operations; one level-triggered `DockerPlanController` applies them
idempotently; one events-plus-inspect observer reports the result. The base owns dependency
ordering, plan-derived readiness gates, `Blocked` propagation, startup rollback, non-destructive
stop, and destructive uninstall.

The plan is the only declarative desired-state document. Artifact identity, resolved mounts,
rotating bootstrap/trust material, the immutable observed-dependency snapshot, and Docker options
are explicit reconciliation inputs beside it; they do not become a second compiler dispatch
surface and are not written back into the plan. Lifecycle supervision is a controller concern:
the resource on `IResourceControlContext` exposes its generic `IManifestResource` lifecycle data,
so the controller path can preserve restart and exit-code semantics without teaching the pure
`DockerPlanCompiler` about manifests.

## Commitments

- **One plan-only compiler, independent of resource identity.** The compiler consumes every
  supported v1 plan without dispatching on manifest kind, resource area, capability interface, or
  CLR type. Unsupported schema or specification values fail before image acquisition or engine
  contact. Unknown Docker hint keys are returned as compiler warnings, emitted once per key in a
  gateway session, and otherwise ignored.
- **One-container topology.** Every accepted plan compiles to one resource container attached to
  the application network. Deployment and StatefulSet semantics are represented only where the
  single-engine topology can preserve them; unsupported replica shapes are rejected, never
  collapsed silently. `DaemonSet` explicitly means one container on this one engine. `Job` means
  a run-once container whose successful exit satisfies its `Stopped` readiness gate rather than a
  long-running service disguised as a task.
- **Application-scoped network and discovery.** One deterministic network is shared by the
  application's built-in Docker resources. The controller attaches stable, resource-derived
  aliases, and observed dependency snapshots project those aliases through the canonical
  `System.Uri` runtime contract. Public endpoints receive host bindings and their observed URLs use
  the configured public host; private endpoints are not published merely for discovery.
- **Gateway-reachable probes.** Docker image publication cannot be relied on to emit a native
  `HEALTHCHECK`, and Docker Desktop/Podman hosts cannot portably reach container IPs. Each endpoint
  targeted by readiness, startup, liveness, or default-control-plane probing therefore receives a
  loopback-only ephemeral host binding (`127.0.0.1:0 -> containerPort`), even when the endpoint is
  private. A public endpoint also receives its public host binding; the probe binding remains
  loopback-only and is not promoted into discovery. HTTPS health probes check reachability and
  status without validating the resource certificate, matching Kubernetes probe semantics: the
  only connection target is the daemon-owned loopback binding, while the certificate is normally
  issued for service or public DNS rather than `127.0.0.1`.
- **Claims and sensitive inputs have distinct intended storage.** Plan claims become deterministic
  Docker named volumes and survive ordinary stop/restart. Configuration files are archived into
  the stopped container before PID 1 starts. For Secret mounts and the bootstrap credential,
  compilation emits sensitive archive entries and container tmpfs declarations rather than image
  layers, environment values, or named volumes. This describes the intended sensitive storage
  shape, not completed live population: Moby's archive filesystem view and the current post-start
  upload make those files unavailable/non-atomic for PID 1, as detailed under delivery boundaries.
  Bootstrap rotation remains a runtime-input change, not plan drift.
- **Plan hash means plan hash.** `cohesion.io/plan-hash` is calculated only from the canonical
  immutable plan. Ownership labels identify the application, resource, and gateway owner.
  Artifact resolution, Secret/bootstrap rotation, option changes, and observed dependency changes
  may require replacement through separate runtime reconciliation metadata, but never alter or
  impersonate the plan hash.
- **Level-triggered engine ownership.** Reconcile preflights labeled engine objects, creates or
  adopts only objects owned by this application/gateway, converges the network and volumes before
  the container, and replaces a stale plan-owned container when an in-place change is unsafe.
  Names remain human-readable but labels, not names alone, establish identity. Missing objects and
  repeated operations are normal inputs, so both apply and delete are idempotent.
- **Observed state has one Docker writer.** One application-scoped observer consumes the Docker
  event stream and periodically inspects all owned containers. Events provide responsiveness;
  inspect makes the view level-true after event loss, reconnect, or gateway restart. This observer
  is the sole writer of local Docker lifecycle and `ResourceEndpoint` observations through the
  public `InMemoryResourceStateManager`.
- **Probe and restart semantics follow authoritative section 5.** Workload/container state and
  gateway probe results are combined with the exact `ReadinessGate` carried by the plan. A first
  liveness failure on a resource that reached `Running` immediately publishes `Degraded` with the
  probe result. `Degraded` is observable but never gating and never re-gates an admitted dependent.
  Three consecutive liveness failures request a restart unless the manifest policy is `Never`.
  `DockerPlanController` reads `IManifestResource.Manifest.Lifecycle.RestartPolicy` from
  `IResourceControlContext.Resource`; `DockerGateway.ValidateResource` also requires
  `Manifest.Lifecycle.ExitCodes` to be `cohesion/sysexits/v1`, allowing exit 64 (configuration) and
  exit 70 (startup) to remain final. The observer honors `Always`, `OnFailure`, and `Never` with
  bounded backoff. Graceful container stop uses `plan.Workload.StopGraceSeconds`
  (`docker stop -t 30` for the default plan value).
- **BCL-only Engine API.** The typed client covers the Docker surface: image
  pull/load/inspect, container create/start/stop/remove/inspect, network and volume
  create/inspect/remove, and events.
  It uses BCL HTTP primitives over `http`, `https`, Unix-domain sockets (`unix`), and Windows named
  pipes (`npipe`) with source-generated `System.Text.Json`; it does not use reflection-based
  serialization. `/_ping` and `GET /version` are unversioned. The client treats the reported
  `MinAPIVersion` and `ApiVersion` as the engine range, intersects it with its inclusive
  v1.25–v1.51 range, selects the highest mutual version, and caches `/vX.Y` for every versioned
  operation. An omitted engine minimum is treated as 1.0; malformed values or no overlap throw
  `NotSupportedException` before the operation proceeds.
- **Index-resolved, digest-verified image acquisition.** When `ImageIndexPath` is configured, Gather
  reads the strict `cohesion/images/v1` `application.images.json`, verifies its application and
  required lowercase OCI `platform`, resolves the resource's `ArtifactRef.Self` entry, and
  requires the authority-free entry repository/digest to match the manifest. A concrete entry
  registry authority is pinned. A target `ContainerRegistry` applies only when `registry` is
  omitted or null and cannot replace a pinned value. Optional `archive` is resolved relative to
  the index without allowing directory escape and must be omitted when unavailable. A late-bound
  entry without a target registry is accepted only when it carries an archive. The default
  `IImageRealizer` then prefers an already present digest-proven engine image, followed by the
  verified archive, followed by a digest pull. Archive load derives and inspects the immutable
  image ID from the verified config; loaded archives need not acquire a `repository@digest` alias.
  Pull calls
  `POST /images/create` with separate `fromImage=<repository>` and `tag=<digest>` values, drains
  the progress stream, then inspects the canonical reference and requires its repository/digest in
  `RepoDigests`, accepting Docker's equivalent `docker.io[/library]` normalization.
  Both paths return the engine ID used at create. `ImageArchives` remains the no-index compatibility
  bridge. A custom `IImageRealizer` replaces default engine acquisition, but the manifest remains
  digest-pinned and its result cannot substitute another repository/digest. When an index is
  configured it receives the reference only after application, own-entry, manifest identity, and
  registry resolution and must return that same indexed repository and digest.
- **Daemon-free direct render API.** `IDockerComposeRenderer.Render(...)` compiles the same plan
  shape into a deterministic Compose-style document without creating an Engine API client or
  contacting a daemon. The rendering is review/debug output; it cannot become a second desired-
  state source and does not make Compose a controller. It is not currently reachable through
  application-set `--mode render`, because the pinned upstream assembly does not carry the
  application renderer contract described under delivery boundaries.
- **Static provider discovery.** The package contributes a `docker` `CohesionGatewayProvider`
  item from `buildTransitive`, naming the gateway and options types and declaring
  `RequiresJit=false`. `Sdk.Gateway` can generate its provider switch without an upstream
  platform-type reference.

## Why-this-not-that decisions

- **BCL transports, not Docker.DotNet.** The required Engine API is small and stable enough for a
  typed internal client. Docker.DotNet would add a much larger dependency/serialization surface
  and weaken trim/AOT control without providing controller semantics.
- **BCL transports, not Cohesion's connection stack in this delivery.** Earlier Docker documents
  preferred Cohesion HTTP and connection packages. That preference is revised because the
  canonical `Assimalign.Cohesion.Connections.NamedPipes` pack is unavailable in the package set
  this repository can consume. Combining that stack for Unix sockets with a private named-pipe
  adapter would create two transport abstractions. BCL `HttpClient`/`SocketsHttpHandler` transport
  hooks instead provide one shippable AOT-safe HTTP path for TCP, Unix sockets, and named pipes.
  Revisit only when a canonical published package can cover the complete client boundary without
  duplicating it.
- **Gateway probes, not container-native health.** The plan is authoritative, the image build path
  cannot promise a Docker `HEALTHCHECK`, and private endpoints still need host-side observation.
  Loopback-only host bindings keep those probes reachable without making them public.
- **Events plus inspect, not either alone.** Events alone lose level truth across disconnects;
  polling alone delays state. One observer combines both and prevents competing state writers.
- **One plan controller, not capability dispatch.** Domain controllers registered in
  `ApplicationGatewayOptions.Controllers` are consulted in registration order before the built-in
  Docker plan controller. The first `CanRealize(plan, out reason)` match wins. A platform x
  resource-area controller matrix is not introduced. Reading generic lifecycle fields from the
  resource already present on the control context does not make the compiler manifest-based and
  does not introduce resource-area code.
- **Stop is not teardown.** `StopAsync` gracefully stops running containers and releases observer
  and supervisor lifetimes while retaining the application network, named claim volumes, and
  owned engine state. `UninstallAsync` removes reached containers, owned volumes, and finally the
  network in best-effort reverse order. Every remaining deletion is attempted after a failure,
  the first failure is reported afterward, and a later retry is safe.
- **Compose-style output, not Compose interop.** The application model and its validated plans are
  the composition source. Render borrows a familiar daemon-free shape; `docker compose up/down`
  does not participate in ownership or reconciliation.

## AOT posture

This project explicitly sets `<IsAotCompatible>true</IsAotCompatible>` and its provider metadata
sets `RequiresJit=false`. This is a scoped design item 36 choice, not a reversal of the 2026-07-20
repository decision that cohesion-platforms carries no default AOT mandate. The promise is viable
because the Engine API client is BCL-only, JSON metadata is source-generated, and provider
selection is static.

There is a separate upstream integration gap: `Sdk.Gateway` must decide `PublishAot` before package
provider metadata is fully resolved, and its early `auto` allowlist currently recognizes only
Local and InProcess. Docker is therefore AOT-compatible without yet being auto-selected for AOT by
that SDK. Explicit `CohesionGatewayAot=true` remains the consumer escape hatch until Docker joins
the upstream allowlist.

## Non-goals

- No Swarm or multi-engine scheduler. `DaemonSet` has the documented one-container Docker meaning.
- No Compose-managed lifecycle. The direct Compose-style renderer is daemon-free diagnostic output
  only; the currently pinned package cannot connect it to application-set `--mode render`.
- No resource-area assembly loading or Docker-specific planner. The area planner already produced
  the generic `ResourcePlan`.
- No claim of real-daemon end-to-end completion in item 36. A compatible Podman/Docker smoke test
  skips cleanly when its endpoint is absent; the full harness remains `L04.01.04.05`.

## Delivery boundary and upstream gaps

Design item 36 reuses `L04.01.04` / cohesion-platforms#23 and incorporates the former Docker
`.01` through `.04` client, gateway/image bridge, compiler/controller, and observer slices. Design
item 35 adds the shared image-index resolution and digest-pull path described above. The Podman
end-to-end harness remains separately tracked as `.05`; the shared embedded registry and general
OCI-store responsibilities remain in `Gateway.Containers`.

The absence of restart policy from `ResourcePlan` is not an implementation gap. The compiler stays
strictly plan-based, while the controller reads
`IManifestResource.Manifest.Lifecycle.RestartPolicy` from `IResourceControlContext.Resource` and
gateway validation pins `Manifest.Lifecycle.ExitCodes` to `cohesion/sysexits/v1`. The observer can
therefore implement `Always`, `OnFailure`, and `Never` and treat configuration/startup exits as
final without changing the plan schema or loading a resource-area assembly.

The following criteria cannot be completed honestly inside this package against the current
engine and upstream package surfaces:

- **Live sensitive-input population.** Compilation emits `HostConfig.Tmpfs` entries and a tar
  containing Secret/bootstrap files. The controller stages non-sensitive Configuration files while
  the container is stopped, then starts it and sends the sensitive tar to
  `PUT /containers/{id}/archive`. Moby's
  [`openContainerFS`](https://github.com/moby/moby/blob/master/daemon/containerfs_linux.go) opens a
  filesystem view with its own private tmpfs mounts; files written there are not visible to the
  container's processes or other views. The post-start order also cannot make PID 1 bootstrap
  atomic. Reliable portable population needs either an image-cooperative helper/entrypoint that
  writes into the live namespace before releasing PID 1, or an upstream engine/gateway seam that
  can stage the live mount before PID 1 runs. Until then, the implementation reserves the secure
  storage shape but does not claim functioning live Secret/bootstrap materialization.
- **Application-set render dispatch.** The project currently resolves the canonical
  `Assimalign.Cohesion.ApplicationModel` `10.0.1-preview.3` DLL. That immutable assembly does not
  export `IApplicationGatewayRenderer`, even though the sibling cohesion checkout contains the
  interface and the application-set `RenderAsync` dispatch in source. Compiling against the
  package therefore prevents `DockerGateway` from implementing the upstream discovery contract.
  `IDockerComposeRenderer.Render(...)` remains public, deterministic, and daemon-free, but
  `--mode render --gateway docker` cannot invoke it until a newly published upstream package
  contains the interface and this repository advances its Cohesion package floor.
- The plan omits the manifest's default control-plane endpoint/path. Explicit plan probes compile
  one-for-one, but the Docker compiler cannot synthesize the implicit default-control-plane
  readiness/liveness/startup probe when a role has no explicit `ProbeMapping`.
- Private service/endpoint values in the plan do not retain their URI scheme. The compiler can
  allocate transport/port bindings, but cannot reconstruct a canonical private `System.Uri`
  dependency value without that scheme.
- The current upstream `Sdk.Gateway` early auto-AOT allowlist omits Docker even though this package
  is `IsAotCompatible=true` and advertises `RequiresJit=false`.
- `ContainerRegistry` supplies only an authority. `PullByDigestAsync` does not yet send
  `X-Registry-Auth`, so authenticated private registries require a future explicit credential
  seam rather than ambient Docker CLI state.
- The archive verifier proves the requested manifest, config, and layer closure before load, and the
  runtime always creates by the verified image ID. Hermetic tests do not establish that a pure
  OCI-layout tar is accepted by every classic and containerd-backed Docker image store; retain
  that case in the real-daemon compatibility matrix.
- The gateway base exposes neither a teardown outcome nor a shared-object release signal from a
  custom controller. A custom-controlled Docker resource can share the application network, so a
  built-in-only success is insufficient proof that the network is safe to remove. Custom-only and
  mixed-controller sessions retain the shared network conservatively until the base carries every
  reached controller's delete outcome.

These are explicit compatibility boundaries, not permission for Docker to load a resource-area
assembly, dispatch by kind or CLR type, parse hidden command-line state, or invent fields outside
the generic plan contract.
