# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Overview

The Docker platform gateway: `DockerGateway : ApplicationGateway` (`Name = "docker"`) compiles
each validated `cohesion/plan/v1` `ResourcePlan` into operations for a Docker-compatible engine and
reconciles them through one controller. Podman's compatible API is the supported local target.

**Design items 35 (image acquisition / #32) and 36 (`L04.01.04` / #23) delivery:**

- `DockerPlanCompiler` — one pure, plan-only compiler for every supported v1 plan, never keyed on
  manifest kind, resource area, capability, or CLR type. It emits one application network and one
  container per accepted resource plan (`DaemonSet` is one container; `Job` is run once), stable
  aliases, named claim volumes, Secret/bootstrap tmpfs declarations and archive entries, public
  host ports, loopback-only probe ports, gateway probes, stop grace, and deterministic ownership
  metadata. Unsupported replica semantics are rejected instead of silently collapsed. The current
  live-tmpfs population limitation is described below.
- `DockerPlanController` — level-triggered, ownership-aware network/volume/container convergence,
  safe container replacement, and idempotent best-effort reverse deletion. Registered domain
  controllers from `ApplicationGatewayOptions.Controllers` are consulted before this built-in
  controller. Only the immutable plan contributes to `cohesion.io/plan-hash`; rotating inputs and
  observations use separate runtime reconciliation state.
- Minimal Engine API client — BCL HTTP over `http`, `https`, Unix sockets, and Windows named pipes,
  with source-generated `System.Text.Json`. Unversioned `GET /version` supplies the engine's
  `MinAPIVersion`–`ApiVersion` range; the client intersects it with supported v1.25–v1.51, chooses
  the highest mutual version, and caches the resulting `/vX.Y` prefix. Invalid version values or a
  disjoint range fail before versioned Engine API use. This supersedes the older preference for
  Cohesion's connection stack because the canonical `Connections.NamedPipes` pack is unavailable
  in the consumed package set; it also avoids Docker.DotNet's broad dependency and serializer
  surface.
- One observer/supervisor — Docker events plus periodic full inspect publish local lifecycle and
  endpoint observations through `InMemoryResourceStateManager`. Probes execute in the gateway.
  The first liveness failure after `Running` publishes non-gating `Degraded`; three consecutive
  failures request restart, following authoritative runtime-contract section 5. The controller
  reads `IManifestResource.Manifest.Lifecycle.RestartPolicy` from the resource carried by
  `IResourceControlContext`, while gateway validation requires `Lifecycle.ExitCodes` to equal
  `cohesion/sysexits/v1`. The observer can therefore apply `Always`, `OnFailure`, and `Never` and
  classify configuration/startup exits as final without adding manifest data to the pure compiler.
  Initial readiness remains the only dependency gate.
- Image acquisition — `DockerGatewayOptions.ImageIndexPath` selects the shared
  `application.images.json` contract. Gather resolves the resource's `ArtifactRef.Self` entry,
  requires its application and source repository/digest to agree with the manifest, applies the
  optional `ContainerRegistry` authority only to `<late-bound>` entries, and resolves an optional
  `archivePath` relative to the index. The default realizer reuses an engine image only after
  proving its digest; otherwise it verifies and loads the indexed archive, or asks the Engine API
  to pull repository plus digest and verifies the resulting `RepoDigests`. In every case it
  returns the immutable engine ID used to create the container. `ImageArchives` remains the legacy
  no-index bridge. An injected `IImageRealizer` replaces engine acquisition but cannot resolve a
  tag-only manifest or substitute its repository/digest. Indexed application, ownership, identity,
  and registry-binding validation still precede that custom call.
- Daemon-free render API — `IDockerComposeRenderer.Render(...)` produces a deterministic
  Compose-style document from one compiled plan and resolved inputs without connecting to an
  engine. Compose is only the output shape; the application model remains the source and
  `DockerPlanController` remains the lifecycle owner. This direct API is not currently connected
  to application-set `--mode render` because of the pinned upstream package boundary below.
- NuGet `buildTransitive` metadata — contributes the statically generated `docker` provider with
  `RequiresJit=false`.

**Dependencies:** generic ApplicationModel and Gateway base packages plus
`platforms/Containers`. COHPLT001 forbids resource-area `.ApplicationModel`, `*.Hosting`,
`.Application` runtime, and `Microsoft.Extensions.*` assemblies from the resolved closure.

**Lifecycle:** `StopAsync` gracefully stops containers with `plan.Workload.StopGraceSeconds`
(`docker stop -t 30` for the default plan), releases the event/probe/restart lifetimes, and retains
the application network, named volumes, and persistent engine state. `UninstallAsync`
(`--mode teardown`) removes reached containers, owned volumes, and then the application network in
best-effort reverse order. Missing objects are success, later deletes still run after a failure,
and retry is safe. Because the gateway base cannot report custom-controller delete outcomes, a
custom-only or mixed-controller application retains its shared network rather than deleting it
without proof that every participant completed teardown.

**AOT:** this project explicitly sets `<IsAotCompatible>true</IsAotCompatible>` as a scoped design
item 36 exception to the repository default of no `IsAotCompatible` mandate. The client is BCL-only,
JSON is source-generated, and provider metadata says `RequiresJit=false`. Upstream `Sdk.Gateway`
still recognizes only Local and InProcess in its early auto-AOT allowlist, so Docker consumers must
select AOT explicitly until that upstream list includes Docker.

## Compatibility boundaries

- The plan omits the default control-plane endpoint/path and private endpoint URI scheme. Explicit
  probes map one-for-one, but an absent-role control-plane probe and canonical private dependency
  URI cannot be reconstructed from plan data today. The missing plan restart-policy field is not
  a blocker: the controller reads the policy from the generic manifest on the control context;
  `DockerPlanCompiler` itself remains `ResourcePlan`-based.
- The controller stages ordinary Configuration files into the stopped container. Sensitive paths
  are declared in `HostConfig.Tmpfs`, after which the controller starts the container and uploads
  the Secret/bootstrap tar through `PUT /containers/{id}/archive`.
  Moby implements that endpoint through `openContainerFS`, whose filesystem view has private tmpfs
  mounts; files extracted below them are not visible to processes in the running container. Even
  on an engine with different archive behavior, the post-start upload is not atomic with PID 1.
  Reliable live Secret/bootstrap population therefore requires an image-cooperative helper or an
  upstream seam that can populate the live mount before PID 1 proceeds. Current tmpfs declarations
  and archive generation are not a claim of completed live sensitive-input materialization.
- This repository resolves the canonical `Assimalign.Cohesion.ApplicationModel`
  `10.0.1-preview.3` assembly, which does not export `IApplicationGatewayRenderer`; the sibling
  cohesion checkout has that interface in source, but it is outside the immutable package surface.
  Consequently `DockerGateway` cannot implement the application-set renderer contract at this
  package floor. Its public `IDockerComposeRenderer.Render(...)` entry point is daemon-free and
  deterministic, but `--mode render --gateway docker` is not wired until a new upstream package
  publishes the contract and the floor is advanced.
- `Sdk.Gateway`'s current early auto-AOT allowlist does not include Docker; `RequiresJit=false`
  provider metadata alone arrives too late to make that SDK-level default selection.
- The application gateway base has no custom-controller teardown outcome for a shared application
  object. Safe network deletion for custom-only and mixed-controller models needs that upstream
  seam.
- Engine digest pull currently has no `X-Registry-Auth` option; `ContainerRegistry` is an address,
  not a credential source. Private-registry acquisition needs an explicit AOT-safe auth seam.
- The hermetic archive tests prove manifest/config/layer digest verification and run-by-ID behavior, but do not
  prove that every supported classic or containerd-backed Docker daemon accepts a pure OCI-layout
  tar through `/images/load`. The real-daemon matrix remains the compatibility authority.

**Status:** design item 36 incorporates the former Docker `.01` through `.04` work items, while
item 35 supplies indexed acquisition and digest pull. Their hermetic coverage and cleanly skipped
daemon smoke do not claim real Docker/Podman end-to-end completion; that remains `.05` / #28.
