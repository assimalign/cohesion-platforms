# Deployment Platforms Program Plan (`L04.01`)

Multi-session orchestration index for implementing Cohesion's **Kubernetes and Docker platform gateways** in this repo. One issue = one session = one branch = one PR. Stages are dependency gates; lanes inside a stage can run in parallel. Durable design outcomes get folded into per-project `docs/DESIGN.md` files as they land; this document tracks sequencing and cross-repo dependencies.

The authoritative architecture is `docs/DEVELOPER_EXPERIENCE_DESIGN.md` (signed off 2026-09-06) in the **cohesion** repo, with `docs/REALIZATION_PLAN.md` defining the `cohesion/plan/v1` compiler input and `docs/RUNTIME_CONTRACT.md` defining the frozen gateway/resource wire contract. Cohesion owns planning and the guided `ApplicationGateway` base; this repository owns exactly one compiler and controller per target platform plus their shared container machinery. Older ApplicationModel design text yields where it disagrees.

## Work-item map

Program root: **[#1](https://github.com/assimalign/cohesion-platforms/issues/1) `[L04.01.00] Cohesion - Deployment Platforms`** in the shared org Project #13, `Codebase = cohesion-platforms`, issues on `assimalign/cohesion-platforms`. (The shared `Area` field stays unset on platform items; grouping uses `Codebase` + the WBS prefix.) Hierarchy is held by native sub-issue links; tasks (`.PP`) are filed during implementation as work is discovered.

| WBS | Item | Wave | Priority | Issue |
| --- | --- | --- | --- | --- |
| **L04.01.01** | **Platforms - Delivery** (area epic) | W01 | P001 | [#2](https://github.com/assimalign/cohesion-platforms/issues/2) |
| L04.01.01.01 | Wire the centralized MSBuild build chain | W01 | P001 | [#3](https://github.com/assimalign/cohesion-platforms/issues/3) |
| L04.01.01.02 | Consume cohesion packages (feed + central pins) | W01 | P001 | [#4](https://github.com/assimalign/cohesion-platforms/issues/4) |
| L04.01.01.03 | CI pipelines (composite action + per-area workflows) | W01 | P002 | [#5](https://github.com/assimalign/cohesion-platforms/issues/5) |
| L04.01.01.04 | Repo governance: README, templates, labels | W01 | P003 | [#6](https://github.com/assimalign/cohesion-platforms/issues/6) |
| L04.01.01.05 | Re-pin, public state manager, platform rules + COHPLT001 (design item 33) | Off-gate | — | [#30](https://github.com/assimalign/cohesion-platforms/issues/30) |
| **L04.01.02** | **Platforms - Containers** (area epic) | W02 | P002 | [#7](https://github.com/assimalign/cohesion-platforms/issues/7) |
| L04.01.02.01 | Container artifact + image-index model (incorporated by design item 35) | W02 | P002 | [#8](https://github.com/assimalign/cohesion-platforms/issues/8) |
| L04.01.02.02 | Gateway state manager + shared test primitives | W02 | P002 | [#9](https://github.com/assimalign/cohesion-platforms/issues/9) |
| L04.01.02.03 | Sample containerized resource + manifest | W02 | P002 | [#10](https://github.com/assimalign/cohesion-platforms/issues/10) |
| L04.01.02.04 | OCI image store (incorporated by design item 35) | W03 | P002 | [#11](https://github.com/assimalign/cohesion-platforms/issues/11) |
| L04.01.02.05 | Image-gathering build seam (consumer incorporated by design item 35; SDK producer remains upstream) | W03 | P003 | [#12](https://github.com/assimalign/cohesion-platforms/issues/12) |
| L04.01.02.06 | Embedded OCI Distribution registry (incorporated by design item 35) | W04 | P004 | [#13](https://github.com/assimalign/cohesion-platforms/issues/13) |
| L04.01.02.07 | Image indexes, OCI store, embedded registry, and digest acquisition paths (design item 35) | Off-gate | — | [#32](https://github.com/assimalign/cohesion-platforms/issues/32) |
| **L04.01.03** | **Platforms - Kubernetes** (area epic) | W02 | P002 | [#14](https://github.com/assimalign/cohesion-platforms/issues/14) |
| L04.01.03.01 | ~~KubernetesClient AOT/trim spike~~ (closed — obsolete once the AOT mandate was dropped, 2026-07-20) | W02 | P002 | [#15](https://github.com/assimalign/cohesion-platforms/issues/15) |
| L04.01.03.02 | Kubernetes connection/options + namespace skeleton (pre-plan) | W02 | P002 | [#16](https://github.com/assimalign/cohesion-platforms/issues/16) |
| L04.01.03.03 | Kubernetes plan compiler/controller foundation (incorporated by design item 34) | W03 | P002 | [#17](https://github.com/assimalign/cohesion-platforms/issues/17) |
| L04.01.03.04 | Informer + readiness observer (incorporated by design item 34) | W03 | P002 | [#18](https://github.com/assimalign/cohesion-platforms/issues/18) |
| L04.01.03.05 | Development image path: daemon-load for Kind | W03 | P002 | [#19](https://github.com/assimalign/cohesion-platforms/issues/19) |
| L04.01.03.06 | Kind-on-Podman end-to-end harness | W03 | P002 | [#20](https://github.com/assimalign/cohesion-platforms/issues/20) |
| L04.01.03.07 | Cross-resource discovery/export/import slice (incorporated by design item 34) | W03 | P003 | [#21](https://github.com/assimalign/cohesion-platforms/issues/21) |
| L04.01.03.08 | Registry reachability topologies (local mirror, in-cluster NodePort) | W04 | P004 | [#22](https://github.com/assimalign/cohesion-platforms/issues/22) |
| L04.01.03.09 | Kubernetes gateway as `KubernetesPlanCompiler` + `KubernetesPlanController` over `ResourcePlan` (design item 34) | W03 | P002 | [#31](https://github.com/assimalign/cohesion-platforms/issues/31) |
| L04.01.03.10 | Expose the gateway control plane on Kubernetes and Docker (design item 37) | Off-gate | — | [#33](https://github.com/assimalign/cohesion-platforms/issues/33) |
| **L04.01.04** | **Platforms - Docker: generic gateway (design item 36; incorporates `.01`–`.04`)** | W03 | P003 | [#23](https://github.com/assimalign/cohesion-platforms/issues/23) |
| L04.01.04.01 | Docker Engine API client (incorporated by design item 36) | W03 | P003 | [#24](https://github.com/assimalign/cohesion-platforms/issues/24) |
| L04.01.04.02 | Docker gateway + image load (incorporated by design item 36) | W03 | P003 | [#25](https://github.com/assimalign/cohesion-platforms/issues/25) |
| L04.01.04.03 | Docker plan compiler + controller (incorporated by design item 36) | W04 | P003 | [#26](https://github.com/assimalign/cohesion-platforms/issues/26) |
| L04.01.04.04 | Docker observer (incorporated by design item 36) | W04 | P003 | [#27](https://github.com/assimalign/cohesion-platforms/issues/27) |
| L04.01.04.05 | Podman end-to-end harness | W04 | P003 | [#28](https://github.com/assimalign/cohesion-platforms/issues/28) |

## Stages

- **Stage 0 — Delivery gate (W01 + off-gate re-pin).** `L04.01.01.01/.02` establish the build and feed. Design item 33 (`L04.01.01.05`) then advances the contract floor to `10.0.1-preview.3`, adopts the public state manager, and lands COHPLT001; it must complete before the Kubernetes or Docker plan compilers. `.03/.04` can trail inside the original stage.
- **Stage 1 — Container foundation + K8s bring-up (W02).** Two lanes: (a) Containers `.01/.02/.03`; (b) Kubernetes `.02` (`.01`, the AOT spike, was closed as obsolete when the repo's AOT mandate was dropped — the skeleton codes against `KubernetesClient` directly).
- **Stage 2 — Kubernetes end-to-end (W03).** Design item 34 (`.09`, incorporating the `.03/.04/.07` slices) plus Kubernetes `.06`, after item 33. Design item 35 (`Containers .07` / #32) incorporates Containers `.04/.05` and Kubernetes `.05`. Exit criterion: the sample application reaches `Running` on Kind-on-Podman through its validated `ResourcePlan`, with dependency ordering, `Blocked` propagation, non-destructive `StopAsync`, and namespace removal through teardown/`UninstallAsync` proven separately. Docker design item 36 (`L04.01.04` / #23, incorporating its `.01`–`.04` slices) may start in parallel after the same re-pin.
- **Stage 3 — Docker end-to-end + registry topologies (W04).** Complete Docker's remaining `.05` real-daemon E2E harness and Kubernetes `.08`; design item 35 incorporates Containers `.06` but does not implement the node-reachable registry topology tracked by Kubernetes `.08` / #22.
- **Off-gate — Control-plane exposure (item 37 / #33).** After items 33–36 and upstream 31t/31b, expose the gateway endpoint, install `cohesion-system`, adopt provider argument hooks and render/bootstrap dispatch, and refresh certificate/trust fixtures. `e2e-kubernetes.yml` incorporates the Kind-on-Podman harness overlap with `L04.01.03.06` / #20; its always-on build, three suites, and render conformance require no cluster.

## Requirements by area

### L04.01.01 — Delivery

The repo uses a two-line root `Directory.Build.props/.targets` → `build/Build.props|targets` → `build/Targets/*` chain, name-only `CohesionProjectReference`, central `CohesionPackageReference` pins, and one `$(CohesionVersion)` for its own outputs. Cohesion dependencies use the immutable release floor `[10.0.1-preview.3, )`; consuming a newer line requires bumping that floor, never replacing an existing package identity. A sibling `../cohesion/_out/packages` feed is appended automatically, and a developer may select a complete `Install-Local.ps1` pack set exactly with `CohesionSiblingPackageVersion=10.0.1-preview.3.local`. CI has no sibling checkout and restores the floor from GitHub Packages. COHPLT001 validates shipped platform projects after reference resolution.

### L04.01.02 — Containers (shared, platform-neutral)

Everything Docker and Kubernetes share, in `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` (+ siblings):

- **Artifact/index model:** strict source-generated readers for per-resource `image.json` and
  gateway-level `application.images.json`, versioned `cohesion/image/v1` and
  `cohesion/images/v1` respectively. Application entries use the same image fields without
  repeating `schema`: required `resource`, authority-free `repository`, `digest`, lowercase OCI
  `platform` (`os/architecture[/variant]`), `aot`, and `baseImage`; optional `registry` and `tag`;
  and optional relative `archive`, which is omitted rather than null when unavailable. An omitted
  or null registry is late-bound; a concrete registry authority is pinned and cannot be replaced
  by a target override. Unknown properties are invalid. Resources are unique, archive paths stay
  relative to the index, and `ArtifactRef.Self` resolves only the plan resource's own entry.
  Images are **always digest-pinned** — a tag is never a pull reference.
- **State manager:** use the public `InMemoryResourceStateManager` from
  `Assimalign.Cohesion.ApplicationModel.Gateway`. The former Containers-owned
  `GatewayResourceStateManager` and its duplicated tests are retired; cohesion owns the reference
  behavior and test matrix.
- **Sample resource:** a minimal enabled HTTP resource that produces the generic manifest and
  `ResourcePlan`, plus a `PublishContainer` OCI image — platform E2E consumes the same facts/plan
  path as customer resources without loading a resource-area assembly into the platform gateway.
- **OCI store + registry:** ingest OCI image-layout directories/tarballs and Docker-save tarballs
  into a content-addressed blob/manifest store, verifying bytes before atomic digest placement.
  A pull-only loopback Registry HTTP API v2 serves repository manifests and reachable blobs by
  digest through `GET`/`HEAD`. It uses a bounded BCL HTTP/1.1 listener: the previously proposed
  `Web.Routing` package's resolved closure includes forbidden `Assimalign.Cohesion.Hosting` and
  would fail COHPLT001.
- **Build seam:** this repo consumes and validates the gathered `application.images.json`; the
  upstream Cohesion SDK item owns `CohesionResourceImageArtifact`, publishing per-resource
  `image.json`, and merging those entries into `<out>/images/` plus the application index.

### L04.01.03 — Kubernetes

`Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` derives from `ApplicationGateway` and supplies:

- **One compiler/controller:** `KubernetesPlanCompiler` purely translates `cohesion/plan/v1` into ConfigMap/Secret/PVC plus the exact Deployment, StatefulSet, DaemonSet, or Job named by `plan.Workload.Kind`, Services (including a governing headless Service), exposures, probes, runtime-contract values, and `cohesion.io/plan-hash`. `KubernetesPlanController` applies the compiled objects idempotently, prunes obsolete owned objects, replaces changed immutable Jobs, and uses an opaque runtime-input revision to roll pods without treating input rotation as plan drift. Neither selects on resource kind, area, or CLR type; unsupported specification/schema fails before gathering or cluster contact, while unknown hints are reported and ignored.
- **Compiler boundary:** the immutable plan is compiled together with the artifact, resolved mounts,
  rotating bootstrap/trust inputs, patches, and immutable observed-dependency snapshot supplied by
  the gateway. Only the plan contributes to `cohesion.io/plan-hash`; secret rotation and observed
  endpoint changes are not plan drift.
- **Provider discovery:** the Kubernetes package contributes `CohesionGatewayProvider` metadata
  from `buildTransitive` (`Name=kubernetes`, concrete gateway/options types,
  `RequiresJit=true`), allowing `Sdk.Gateway` to generate a static switch without an upstream
  platform-type reference.
- **Registered-first escape hatch:** domain-authored controllers from `ApplicationGatewayOptions.Controllers` are consulted in registration order before the built-in plan controller; the framework registers none. The base external controller remains responsible for external-resource observations.
- **Observer:** the single list+watch observer is the sole writer for locally realized Kubernetes observations through the public `InMemoryResourceStateManager`; it starts with a labeled pod list, watches from that resource version, and re-lists after watch failure or `410 Gone`, while a bounded full resync reads workloads, Services, and Service `Endpoints`. It filters overlapping pods by the compiled workload revision, combines plan probes with workload/pod status, requires ready Service addresses, observes liveness failure as non-gating `Degraded`, surfaces current-revision exit/restart information, publishes stable Service-DNS endpoint observations, and reconciles the workload's `PUBLIC_URL` contract value before refreshing exports when public addresses change. Dependency compilation consumes the immutable `ObservedDependencies` snapshot supplied by the base.
- **Lifecycle/ownership:** namespace = application and carries `cohesion.io/owner` under the gateway's field manager; foreign ownership is refused unless adopted, absent-object conflicts are re-read, namespace-wide Service/PVC identities are preflighted, and apply/delete concurrency is guarded by resource-version/UID preconditions. Stateful-generated claims are discovered without relying on Cohesion labels and adopted before workload apply; controller removal is refused while claims retain controller owner references. `StopAsync` releases supervision/observation while preserving objects. Teardown/`UninstallAsync` removes objects, waits for deletion, and commits namespace deletion only after every reached built-in participant reports success, in best-effort reverse order; partial startup rollback is scoped to the resources it reached.
- **Dev image path:** when `IsDevelopment` and the selected context is Kind, resolve the resource's
  own index archive and load it via `kind load image-archive` (`KIND_EXPERIMENTAL_PROVIDER=podman`
  is inherited), with an in-session cluster+digest skip and `imagePullPolicy: IfNotPresent`.
  Absence of `kind` reports and skips the command cleanly, then uses a configured registry or
  refuses unresolved late binding rather than falling through to an implicit registry. Registry
  topologies remain Kubernetes `.08` / #22.
- **Discovery:** observed endpoints are projected through Core's `System.Uri` contract. Kubernetes injects Service DNS (the governing Service for StatefulSets), never NodePort/Ingress allocations for in-cluster dependencies.
- **Application sets:** member models imported from each area gateway's `cohesion-export` are reconciled by the root gateway through one client and observer. The upstream builder refuses `--realize` for Kubernetes before reconcile and directs Development use to Local, InProcess, or Docker.
- **AOT:** none required — the repo-wide AOT mandate was dropped by owner decision (2026-07-20); the locally pinned `KubernetesClient` needs no exception. `.01` was closed as obsolete, and a typed-REST fallback remains only a dependency-hygiene option. Cohesion's stale, unused central `KubernetesClient` pin is not a coordination point.
- **E2E:** Kind on Podman (Podman 5.x machine, Kind ≥ 0.30, `kubectl` present). Cluster lifecycle scripted by the test harness; readiness uses the plan-derived gate (`{Running, Failed, Stopped}` for long-running workloads), `Degraded` never re-gates dependents, stop preserves state, and teardown removes it.
- **Plan completion:** upstream 31t supplies `controlPlane`, endpoint `scheme` and `certificate`,
  and `workload.restartPolicy` in the existing `cohesion/plan/v1` schema. Item 37 consumes those
  facts, validates certificate mounts, and delivers the trust bundle as a protected file.
  Jobs accept `Never` or `OnFailure`; long-running pod templates require `Always` and warn when
  clamping another requested value. The upstream validator still owns the manifest `maxReplicas`
  bound, which is absent from the plan.
- **CLI and installation:** both providers declare the public static `CommandLineApplyMethod`
  hook consumed before upstream control-plane configuration. Kubernetes `--mode render` emits
  the system installation followed by resources; `--mode bootstrap` always emits the system
  installation and applies it by default (`--bootstrap-apply=false` is emit-only). The installation
  owns explicit digest-pinned image, namespace, service-account, storage, and exposure inputs.
- **Upstream lifetime/topology boundary:** the current resolver/model contracts expose neither a
  managed Development port-forward lifetime nor a root-versus-member gateway role. Those
  contracts must land before the package can own those lifetimes or enforce the
  single-production-owner topology.
  The base observer hook also does not expose the lifecycle cause or custom-controller delete
  outcomes, so safe namespace deletion for custom-controller-only/external-only models requires
  an upstream teardown-outcome seam; mixed custom-controller models preserve their namespace
  conservatively.
- **Telemetry payload boundary:** 31b's resolved telemetry endpoint and headers document are
  private/internal to the upstream Gateway assembly. Item 37 provides an empty-by-default
  protected `telemetry.headers` carrier on the existing bootstrap projection; a public/protected
  gateway seam or `ResourceInputs` member is still required to supply it. Platform compilers do
  not infer LogSpace policy, mint telemetry tokens, or set telemetry endpoint/protocol variables.

### L04.01.04 — Docker

`Assimalign.Cohesion.ApplicationModel.Gateway.Docker` is design item 36 (`L04.01.04` / #23),
incorporating the former `.01`–`.04` client, gateway, compiler/controller, and observer slices. The
real-daemon harness remains `.05` / #28:

- **One plan-only compiler:** `DockerPlanCompiler` consumes every supported
  `cohesion/plan/v1` plan through one pure path, never dispatching on manifest kind, resource area,
  capability, or CLR type. Unsupported schema/specification and replica semantics fail before
  image acquisition or engine contact; unknown Docker hints warn once per gateway session and are
  ignored. Each accepted plan produces one container: `DaemonSet` is one container in the
  single-engine topology, while `Job` is a run-once container satisfying on `Stopped`.
- **Compiler boundary and identity:** artifact identity, resolved Configuration/Secret/bootstrap
  inputs, Docker options, and the immutable observed-dependency snapshot remain explicit inputs
  beside the plan. Only the canonical plan contributes to `cohesion.io/plan-hash`; rotation and
  observations use separate runtime reconciliation metadata. Application/resource/owner labels,
  not names alone, identify managed objects. Lifecycle supervision remains outside the compiler:
  the controller reads `plan.Workload.RestartPolicy`, retaining the generic manifest policy only
  for legacy plans that omit the value. Gateway validation requires `Manifest.Lifecycle.ExitCodes`
  to be `cohesion/sysexits/v1`.
- **Network and ports:** one application-scoped network carries stable resource aliases used for
  dependency discovery. Public endpoints receive public host bindings. Every endpoint used by a
  gateway-side readiness, startup, liveness, or default-control-plane probe also receives a
  loopback-only ephemeral binding (`127.0.0.1:0`), including private endpoints; probe bindings are
  never published as dependency addresses.
- **Storage and sensitive inputs:** claims compile to deterministic named volumes. Secret mounts
  and the rotating bootstrap credential compile to `HostConfig.Tmpfs` declarations and sensitive
  tar entries rather than environment values, named volumes, or image layers. Non-sensitive
  Configuration entries are staged into the stopped container before PID 1 starts. Sensitive
  entries cannot use that path because the later tmpfs mount would hide the underlying files;
  post-start archive extraction uses Moby's private `openContainerFS` tmpfs view and is not visible
  to container processes. Atomic PID 1 bootstrap therefore requires an image-cooperative helper or
  an upstream engine/gateway staging seam.
- **Client:** a minimal typed Docker Engine API client uses BCL HTTP primitives over `http`,
  `https`, Unix-domain sockets, and Windows named pipes, with source-generated `System.Text.Json`.
  Scope is image load/inspect, container create/start/stop/remove/inspect, network and volume
  operations, and events. Unversioned `GET /version` negotiates the highest overlap between the
  engine's reported API range and the client's supported v1.25–v1.51 range; the selected `/vX.Y`
  prefix is cached, and malformed or disjoint ranges fail before versioned API use. This explicitly
  revises the older Cohesion connection-stack preference: the canonical
  `Assimalign.Cohesion.Connections.NamedPipes` pack is unavailable in the consumed package set,
  while the BCL path provides one shippable AOT-safe transport surface. Docker.DotNet remains
  disfavored for its broad dependency and serializer surface.
- **Controller and render:** `DockerPlanController` converges network, volume, and container state
  idempotently; registered domain controllers are consulted first. The public
  `IDockerComposeRenderer.Render(...)` compiles one plan into a deterministic Compose-style
  document without opening an Engine API connection. Compose is an output shape, not the desired-
  state source or an alternate lifecycle owner. Item 37 implements `IApplicationGatewayRenderer`
  for offline application-set `--mode render`, using the upstream dispatcher and the provider's
  shared public command-line hook.
- **Observer and probes:** one events-plus-periodic-inspect observer is the sole writer of locally
  realized Docker lifecycle/endpoints through `InMemoryResourceStateManager`. Gateway probes and
  container state implement the plan gate. Per authoritative runtime-contract section 5, the first
  liveness failure after `Running` publishes non-gating `Degraded`; three consecutive failures
  request a supervised restart subject to the manifest's `Always`, `OnFailure`, or `Never` policy.
  Under `cohesion/sysexits/v1`, configuration exit 64 and startup exit 70 are final. Dependents are
  gated only on initial readiness.
- **Lifecycle/ownership:** `StopAsync` gracefully stops containers with
  `plan.Workload.StopGraceSeconds` (`docker stop -t 30` for the default plan), releases observation
  and supervision, and retains the application network, named volumes, and persistent engine
  state. `UninstallAsync` deletes reached containers, owned volumes, and then the application
  network in idempotent best-effort reverse order, attempts later deletes after a failure, and is
  safe to retry.
- **Image acquisition:** design item 35 resolves the resource's own `cohesion/images/v1`
  `application.images.json` entry and optional relative `archive`. Its authority-free repository
  is combined with the entry's pinned registry, or with `ContainerRegistry` only when the entry
  registry is omitted or null. The default `IImageRealizer` verifies the archive's
  manifest/config/layer closure, loads and inspects the immutable engine image ID, and creates by ID even when
  the daemon assigns no `repository@digest` alias. Registry-backed entries use Engine API
  `PullByDigestAsync` and verify the requested digest through `RepoDigests`. The earlier explicit
  `ImageArchives` map remains a compatibility bridge when no application index is configured.
- **Provider/AOT:** the package contributes the `docker` provider through `buildTransitive` with
  `RequiresJit=false`. Its project explicitly sets `<IsAotCompatible>true</IsAotCompatible>` as a
  scoped design item 36 exception to this repo's default of no AOT mandate; this does not reinstate
  a repository-wide rule. The current upstream `Sdk.Gateway` early auto-AOT allowlist recognizes
  only Local/InProcess, so Docker still requires an explicit AOT selection until that SDK gap is
  closed.
- **Plan completion and control plane:** item 37 consumes the upstream 31t control-plane path,
  endpoint scheme/certificate, and restart policy. Docker binds the upstream application control
  plane on loopback by default, with explicit `--control-plane-bind` for host/LAN exposure; the
  upstream server writes `control-plane.json` beside `export.json`. Certificate bundles, trust
  anchors, and optional telemetry headers remain sensitive files on tmpfs. The telemetry carrier
  has no production payload until upstream exposes 31b's private resolved inputs, and platform
  compilers leave telemetry endpoint/protocol unset.
- **Upstream teardown boundary:** the base gateway does not expose a custom-controller delete
  outcome for shared application objects. A custom-controlled resource may share the Docker
  network, so custom-only and mixed-controller sessions conservatively retain that network until
  every reached controller can report teardown success.
- **E2E boundary:** hermetic compiler/controller/client/observer tests and a daemon smoke test that
  skips cleanly when no compatible endpoint is available are item 36's honest boundary. Real
  Podman/Docker end-to-end proof of plan gates, ordering, `Blocked`, `Degraded`, stop, and teardown
  remains `.05` / #28.

## Cross-repo dependencies (cohesion)

1. **Package feed** — immutable `10.0.1-preview.3` Cohesion packages are present in the sibling feed and GitHub Packages. This repo consumes a `>=` floor and optionally an exact, complete `.local` sibling identity; a newer line requires a new package version and a pin bump.
2. **State manager visibility (resolved)** — cohesion made `InMemoryResourceStateManager` public in design item 18. This repo consumes it and has deleted the former Containers-owned copy.
3. **Fresh package set for compiler work** — item 34 consumes the preview.3 contracts packed by
   item 33. Later items likewise require a fresh, complete cohesion pack so platform compilers
   exercise the latest gateway, plan, control-plane, and input-resolution surfaces.
4. **HTTP stack for the registry (resolved differently)** — design item 35 selected a bounded BCL
   loopback HTTP/1.1 listener under the then-current Hosting closure restriction, consistent with
   the Docker Engine client precedent. The later base-hosting guard correction does not change
   that implementation or extend rule 3's direct-reference allowlist.
5. **Plan conformance fixtures** — vendor cohesion's checked-in `KindMatrixTests` plan fixtures when compiler implementation begins, so Docker and Kubernetes prove byte-equivalent generic-plan semantics without loading any `<Area>.ApplicationModel` assembly.
6. **Publication gate after 31t/31b** — the refreshed fixtures require the completed v1 contract; CI restoring the older preview fails. The required `[10.0.1-preview.3, )` floor remains unchanged and permits compatible later previews. Publication alone does not guarantee selection while the old minimum is available: NuGet applies its [lowest applicable version rule](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution#lowest-applicable-version) to non-floating ranges.
7. **Base-hosting closure correction (resolved, 2026-09-14)** — commit `0c186b2` aligned COHPLT001 and rule 3 with the allowed Gateway base's legitimate Hosting, Hosting.Health, and Hosting.Resources dependencies. The whole solution builds against the fresh `.local` closure with zero COHPLT001 failures; platform projects still never reference the base-hosting trio directly.

## Environment (local dev)

- Podman 5.8.x machine (WSL) running; Kind 0.30.x with `KIND_EXPERIMENTAL_PROVIDER=podman`; kubectl 1.34.x; .NET SDK per `global.json`.
- Podman's Docker-compatible socket serves the Docker gateway E2E.

## Progress log

| Date | Item | Note |
| --- | --- | --- |
| 2026-07-20 | Program plan | Authored; work items filed to Project #13 under `L04.01` with the new `Codebase` field. |
| 2026-07-20 | Delivery (#3–#6) | Build chain, package feed, scaffold, CI landed; all three `platform-*` workflows green on the 3-OS matrix. |
| 2026-07-20 | #9 / #16 | `GatewayResourceStateManager` + Kubernetes gateway skeleton landed (22 tests); Kind-on-Podman cluster `cohesion-dev` verified; upstream proposal cohesion#935 filed. |
| 2026-07-20 | AOT decision | Owner decision: this repo carries **no** `IsAotCompatible` mandate (gateways are deploy-time control planes; cohesion libraries keep it). #15 closed as obsolete; rules/docs updated; sample resource runtime keeps the cohesion posture. |
| 2026-09-09 | #30 / design item 33 | Re-pinned to preview.3, retired `GatewayResourceStateManager` for cohesion's public implementation, aligned plan-compiler rules/docs, and activated COHPLT001. This precedes the Kubernetes and Docker compiler items. |
| 2026-09-09 | #31 / design item 34 | Replaced the Kubernetes pre-plan controller direction with one generic `KubernetesPlanCompiler` + `KubernetesPlanController`, added static gateway-provider package metadata, and recorded the remaining upstream plan-schema and CLI seams. |
| 2026-09-10 | #23 / design item 36 | Consolidated Docker `.01`–`.04` into one AOT-compatible generic plan compiler/controller delivery: BCL Engine API client with `/version` negotiation across the mutual v1.25–v1.51 range, app network/container/volume/tmpfs/port compilation, events+inspect observation and manifest-backed section-5 restart semantics, a public daemon-free renderer, and the explicit digest-verified OCI archive bridge pending item 35. Recorded the live-tmpfs/PID 1 staging and canonical preview.3 renderer-contract blockers plus the remaining schema, SDK auto-AOT, and custom-controller teardown gaps; `.05` remains the real-daemon E2E harness. |
| 2026-09-10 | #32 / design item 35 | Added the exact `cohesion/image/v1` per-resource and `cohesion/images/v1` application index consumer contracts, strict own-resource resolution for `ArtifactRef.Self`, digest-verifying OCI/docker-save disk store, pull-only embedded Registry v2, Docker archive/pull acquisition, and Development Kind archive loading. The Cohesion SDK remains the producer, and Kubernetes `.08` / #22 retains registry reachability. |
