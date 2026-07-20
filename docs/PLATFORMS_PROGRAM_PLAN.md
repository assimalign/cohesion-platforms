# Deployment Platforms Program Plan (`L04.01`)

Multi-session orchestration index for implementing Cohesion's **Kubernetes and Docker platform gateways** in this repo. One issue = one session = one branch = one PR. Stages are dependency gates; lanes inside a stage can run in parallel. Durable design outcomes get folded into per-project `docs/DESIGN.md` files as they land; this document tracks sequencing and cross-repo dependencies.

The upstream architecture this program implements is `libraries/ApplicationModel/DESIGN.md` (v2.x) in the **cohesion** repo — the contract package `Assimalign.Cohesion.ApplicationModel`, the guided base `Assimalign.Cohesion.ApplicationModel.Gateway` (`ApplicationGateway`), and the planned container-gateway design (per-resource pre-built OCI tarballs, digest-pinned images, embedded OCI registry, single-informer observation). Phases 1–2 (contracts + base/LocalGateway) are done upstream; this program is the platform half of Phase 6, plus the shared container machinery it needs.

## Work-item map

Program root: **[#1](https://github.com/assimalign/cohesion-platforms/issues/1) `[L04.01.00] Cohesion - Deployment Platforms`** in the shared org Project #13, `Codebase = cohesion-platforms`, issues on `assimalign/cohesion-platforms`. (The shared `Area` field stays unset on platform items; grouping uses `Codebase` + the WBS prefix.) Hierarchy is held by native sub-issue links; tasks (`.PP`) are filed during implementation as work is discovered.

| WBS | Item | Wave | Priority | Issue |
| --- | --- | --- | --- | --- |
| **L04.01.01** | **Platforms - Delivery** (area epic) | W01 | P001 | [#2](https://github.com/assimalign/cohesion-platforms/issues/2) |
| L04.01.01.01 | Wire the centralized MSBuild build chain | W01 | P001 | [#3](https://github.com/assimalign/cohesion-platforms/issues/3) |
| L04.01.01.02 | Consume cohesion packages (feed + central pins) | W01 | P001 | [#4](https://github.com/assimalign/cohesion-platforms/issues/4) |
| L04.01.01.03 | CI pipelines (composite action + per-area workflows) | W01 | P002 | [#5](https://github.com/assimalign/cohesion-platforms/issues/5) |
| L04.01.01.04 | Repo governance: README, templates, labels | W01 | P003 | [#6](https://github.com/assimalign/cohesion-platforms/issues/6) |
| **L04.01.02** | **Platforms - Containers** (area epic) | W02 | P002 | [#7](https://github.com/assimalign/cohesion-platforms/issues/7) |
| L04.01.02.01 | Container artifact + image-index model | W02 | P002 | [#8](https://github.com/assimalign/cohesion-platforms/issues/8) |
| L04.01.02.02 | Gateway state manager + shared test primitives | W02 | P002 | [#9](https://github.com/assimalign/cohesion-platforms/issues/9) |
| L04.01.02.03 | Sample containerized resource + manifest | W02 | P002 | [#10](https://github.com/assimalign/cohesion-platforms/issues/10) |
| L04.01.02.04 | OCI image store (tarball → blob/manifest store) | W03 | P002 | [#11](https://github.com/assimalign/cohesion-platforms/issues/11) |
| L04.01.02.05 | Image-gathering build seam (`application.images.json`) | W03 | P003 | [#12](https://github.com/assimalign/cohesion-platforms/issues/12) |
| L04.01.02.06 | Embedded OCI Distribution registry (Registry HTTP API v2) | W04 | P004 | [#13](https://github.com/assimalign/cohesion-platforms/issues/13) |
| **L04.01.03** | **Platforms - Kubernetes** (area epic) | W02 | P002 | [#14](https://github.com/assimalign/cohesion-platforms/issues/14) |
| L04.01.03.01 | ~~KubernetesClient AOT/trim spike~~ (closed — obsolete once the AOT mandate was dropped, 2026-07-20) | W02 | P002 | [#15](https://github.com/assimalign/cohesion-platforms/issues/15) |
| L04.01.03.02 | Kubernetes gateway skeleton + namespace controller | W02 | P002 | [#16](https://github.com/assimalign/cohesion-platforms/issues/16) |
| L04.01.03.03 | Kubernetes resource controller (SSA ConfigMap/Deployment/Service) | W03 | P002 | [#17](https://github.com/assimalign/cohesion-platforms/issues/17) |
| L04.01.03.04 | Informer + readiness observer (single list+watch) | W03 | P002 | [#18](https://github.com/assimalign/cohesion-platforms/issues/18) |
| L04.01.03.05 | Development image path: daemon-load for Kind | W03 | P002 | [#19](https://github.com/assimalign/cohesion-platforms/issues/19) |
| L04.01.03.06 | Kind-on-Podman end-to-end harness | W03 | P002 | [#20](https://github.com/assimalign/cohesion-platforms/issues/20) |
| L04.01.03.07 | Cross-resource discovery injection (observed endpoints / Service DNS) | W03 | P003 | [#21](https://github.com/assimalign/cohesion-platforms/issues/21) |
| L04.01.03.08 | Registry reachability topologies (local mirror, in-cluster NodePort) | W04 | P004 | [#22](https://github.com/assimalign/cohesion-platforms/issues/22) |
| **L04.01.04** | **Platforms - Docker** (area epic) | W03 | P003 | [#23](https://github.com/assimalign/cohesion-platforms/issues/23) |
| L04.01.04.01 | Docker Engine API client (AOT-safe, npipe/unix socket) | W03 | P003 | [#24](https://github.com/assimalign/cohesion-platforms/issues/24) |
| L04.01.04.02 | Docker gateway + image load | W03 | P003 | [#25](https://github.com/assimalign/cohesion-platforms/issues/25) |
| L04.01.04.03 | Docker container controller (network, containers, ports) | W04 | P003 | [#26](https://github.com/assimalign/cohesion-platforms/issues/26) |
| L04.01.04.04 | Docker observer (events + inspect → lifecycle) | W04 | P003 | [#27](https://github.com/assimalign/cohesion-platforms/issues/27) |
| L04.01.04.05 | Podman end-to-end harness | W04 | P003 | [#28](https://github.com/assimalign/cohesion-platforms/issues/28) |

## Stages

- **Stage 0 — Delivery gate (W01).** `L04.01.01.01/.02` are hard blockers for all code work: the build chain must import, and `Assimalign.Cohesion.ApplicationModel.Gateway` must restore from the feed. `.03/.04` can trail inside the stage.
- **Stage 1 — Container foundation + K8s bring-up (W02).** Two lanes: (a) Containers `.01/.02/.03`; (b) Kubernetes `.02` (`.01`, the AOT spike, was closed as obsolete when the repo's AOT mandate was dropped — the skeleton codes against `KubernetesClient` directly).
- **Stage 2 — Kubernetes end-to-end (W03).** Kubernetes `.03/.04/.05/.06/.07` + Containers `.04/.05`. Exit criterion: the sample application reaches `Running` on a Kind-on-Podman cluster through `Application.CreateBuilder → UseKubernetesGateway → RunAsync`, with dependency ordering, Blocked propagation, and namespace teardown proven by tests. Docker lane `.01/.02` may start in parallel.
- **Stage 3 — Docker end-to-end + registry topologies (W04).** Docker `.03/.04/.05`; Containers `.06`; Kubernetes `.08`.

## Requirements by area

### L04.01.01 — Delivery

The repo builds and consumes cohesion exactly like the cohesion repo builds itself: two-line root `Directory.Build.props/.targets` → `build/Build.props|targets` → `build/Targets/*` chain; name-only `CohesionProjectReference` (globbing `platforms/**`); central `CohesionPackageReference` pins; `$(CohesionVersion)` single source. New here: `nuget.config` mapping `Assimalign.Cohesion.*` → GitHub Packages (`nuget.pkg.github.com/assimalign`) with delete-then-replace staging semantics, and a documented local sibling-feed override (`../cohesion/_out/packages`) for inner-loop work. CI mirrors cohesion's composite-action + thin path-filtered per-area workflow pattern (`platform-*.yml`, 3-OS matrix, `main`+Linux-gated nupkg publish via `Publish-Nupkg.ps1`).

### L04.01.02 — Containers (shared, platform-neutral)

Everything Docker and Kubernetes share, in `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` (+ siblings):

- **Artifact/index model:** `IContainerImageArtifact` implementation; `application.images.json` (ResourceName → `{repository, digest, tag?, archivePath}`) with a source-generated serializer; strict `sha256:` digest validation. Images are **always digest-pinned** — a tag is never a pull reference.
- **State manager:** the Gateway package's reference `InMemoryResourceStateManager` is `internal` upstream, so this repo ships a public equivalent honoring the documented contract (one lock for reads/writes/waiter-registration; waiters completed and `StateChanged` raised outside the lock; terminal-set waits; timeout returns last observed state). Port the upstream test matrix. *(Candidate upstream issue: make the reference implementation public and retire this copy.)*
- **Sample resource:** a minimal HTTP echo runtime + `.ApplicationModel` manifest (implements `IExecutableResource` + `IEndpointResource` + `IMountResource`) with a `PublishContainer` OCI tarball build — keeps platform E2E independent of cohesion's pending Phase-5 manifest codegen.
- **OCI store + registry:** unpack OCI image-layout tarballs into a content-addressed blob/manifest store (digest-verified); later serve it over the Docker Registry HTTP API v2 on Cohesion's own HTTP stack (`Http.Connections` + `Connections.Tcp` + `Web.Routing` — no ASP.NET Core). Registry implementation notes from the upstream stack: use the `HttpResponseStreaming` interceptor for blob GET (default response path buffers whole bodies); raise `MaxRequestBodySize` (default ~28.6 MB) for blob PUT; HEAD semantics come free from the HTTP/1.1 writer.
- **Build seam:** `CohesionResourceImageArtifact` item contract + `CohesionBuildResourceContainers` gather target merging advertised tarballs into `<out>/images/` + `application.images.json`, packaged so orchestrator projects can consume it (`sdks/`). Upstream reserves the seam (`Assimalign.Cohesion.Sdk` ships an inert `ApplicationModel.Build.targets` stub); the metadata-advertising half lives in cohesion manifest packages.

### L04.01.03 — Kubernetes

`Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` derives from `ApplicationGateway` and supplies:

- **Gateway/namespace:** options (kubeconfig path, context, in-cluster); `KubernetesNamespaceController` ensures namespace = `model.Name` (+ pull secret / registry trust when registry topologies land); `StopAsync` deletes the namespace; `UseKubernetesGateway()` extension.
- **Resource controller:** matches `IExecutableResource && IEndpointResource`; server-side-applies `V1ConfigMap` (environment variables), `V1Deployment` (image `{repository}@sha256:{digest}` from `GetArtifact<IContainerImageArtifact>()`), `V1Service` from declared endpoints, mounts → volumes; fixed field manager; pure level-triggered idempotent apply.
- **Observer:** the single list+watch informer is the sole `State` writer; `Running` ⇔ `observedGeneration == generation && updatedReplicas == readyReplicas == spec.replicas`; auto-relist on `410 Gone`; publishes **observed endpoints** (stable Service DNS + ports) on `Running`. Note: observed-endpoint publication exists upstream only as contract surface — no production writer/reader exists yet; this is the first real implementation.
- **Dev image path:** when `IsDevelopment` and the cluster is Kind, load tarballs via `kind load image-archive` (`KIND_EXPERIMENTAL_PROVIDER=podman`), `imagePullPolicy: IfNotPresent`, digest-keyed skip; registry topologies are the W04 production path.
- **Discovery:** dependents receive dependency endpoints at reconcile time from `State.GetObservedEndpoints` — on Kubernetes, inject Service DNS, never NodePort/Ingress allocations.
- **AOT:** none required — the repo-wide AOT mandate was dropped by owner decision (2026-07-20); `KubernetesClient` 17.0.4 (centrally pinned) needs no exception. `.01` was closed as obsolete; the typed-REST fallback survives only as a dependency-hygiene option, and the 17.0.4 CVE (GHSA-w7r3-mgwf-4mqq) bump is coordinated with the cohesion central pin.
- **E2E:** Kind on Podman (Podman 5.x machine, Kind ≥ 0.30, `kubectl` present). Cluster lifecycle scripted by the test harness; full up/down with dependency ordering, readiness gating, Blocked propagation, reverse teardown.

### L04.01.04 — Docker

`Assimalign.Cohesion.ApplicationModel.Gateway.Docker` — the smaller sibling (~80 % shared machinery via Containers):

- **Client:** minimal typed Docker Engine REST client over named pipe (Windows) / Unix socket on Cohesion's own connection stack — source-generated serialization; Podman's Docker-compatible API is the supported local target. Scope: images (load/inspect), containers (create/start/stop/remove/inspect), networks, events. *(Docker.DotNet is disfavored on dependency-hygiene grounds — large surface, external serializer dependency; with the AOT mandate dropped it is no longer categorically excluded.)*
- **Gateway:** `GatherAsync` from `application.images.json`; image load from OCI tarball with digest verification; `UseDockerGateway()`.
- **Controller:** app-scoped network named after `model.Name`; containers created with env/mounts/published ports from capability interfaces; ownership labels; idempotent reconcile.
- **Observer:** Docker events stream reconciled with inspect polling → `ResourceLifecycle`; observed endpoints from port bindings; sole `State` writer.
- **E2E:** against the Podman socket — up/down, ordering, Blocked propagation, teardown removes containers + network.

## Cross-repo dependencies (cohesion)

1. **Package feed** — cohesion CI publishes `Assimalign.Cohesion.*` to GitHub Packages on `main` (delete-then-replace, constant version `10.0.1-preview.2`). Stage 0 consumes it; refreshing upstream bits requires cache-busting restore.
2. **State manager visibility** — `InMemoryResourceStateManager` is internal upstream; we re-implement publicly in Containers. File a cohesion issue proposing it be made public.
3. **Phase-5 manifest codegen + real resource images** — running a *real* resource (e.g., Database) on these gateways needs the cohesion side to add `PublishContainer`/`CohesionResourceImageArtifact` advertisement to its manifest packages. The sample resource (`L04.01.02.03`) decouples this program; wiring Database E2E becomes a follow-up spanning both repos.
4. **HTTP stack for the registry** — `Http.Connections`, `Connections.Tcp`, `Web.Routing`, `Http.Streaming`, `Http.RequestLimits` packages must be on the feed by the time `L04.01.02.06` starts.
5. **`LocalGateway` observed endpoints** — upstream LocalGateway never publishes observed endpoints; once this repo proves the pattern, propose the upstream adoption so all gateways behave uniformly.

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
