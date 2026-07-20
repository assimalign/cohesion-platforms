# Platform Areas (`platforms/**`)

Every `platforms/<Platform>/` folder is a **platform area**: the implementation of Cohesion's application-model gateway contract for one deployment target. This file is the architecture rule for those areas — the analog of the cohesion repo's `resource-areas.md`. The authoritative upstream design is `libraries/ApplicationModel/DESIGN.md` in the cohesion repo; nothing here may contradict it without an explicit user-confirmed deviation.

## The gateway family shape

A platform gateway package is `Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>` living at `platforms/<Platform>/Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>/{src,tests,docs}`. It derives from the public guided base `ApplicationGateway` (package `Assimalign.Cohesion.ApplicationModel.Gateway`) and supplies only the platform hooks:

- `Name` — stable gateway identity (`"kubernetes"`, `"docker"`).
- `Controllers` — an **explicitly registered, priority-ordered** list of `IApplicationResourceController`s. First `CanControl` match wins; the most specific controller is registered first. **Never assembly-scan for controllers.**
- `State` — an `IApplicationResourceStateManager` implementation honoring the contract: reads, writes, and waiter registration under one lock; waiters completed and `StateChanged` raised outside the lock; `WaitForStateAsync` is a terminal-**set** membership wait that returns the last observed state on budget expiry (never throws for timeout).
- `GatherAsync` — **locates and validates** a deployable artifact; it never builds. Container gateways resolve a pre-built, digest-pinned image (via the image index) and return an `IContainerImageArtifact`.
- `StartObserverAsync` / `StopObserverAsync` — the platform's **single observer** (informer, event stream, poller).

## Hard rules

1. **Single-writer observed state.** The gateway's one observer is the *only* writer of observed state into `State`. Controllers apply desired state and return; they never own steady-state or write lifecycle transitions.
2. **Controllers are pure level-triggered reconcilers.** `ReconcileAsync` is idempotent apply-and-return — it never blocks on readiness (the base algorithm gates readiness via `WaitForStateAsync({Running, Failed}, ReadinessBudget)`).
3. **Layering:** a gateway references the ApplicationModel contract package, the Gateway base package, shared `platforms/Containers` libraries, and `{Resource}.ApplicationModel` **manifest** packages only. It must **never** reference a `{Resource}.Application` runtime, and never `Microsoft.Extensions.*`.
4. **Digest-pinned images.** Deployments always reference images as `{repository}@sha256:{digest}`. Tags are human-readable metadata only — never a pull reference.
5. **Observed endpoints are the discovery surface.** When the observer marks a resource `Running`, it publishes the resource's *observed* endpoints via `SetState(..., observedEndpoints)`. Dependent resources receive dependency endpoints at their own reconcile time from `context.State.GetObservedEndpoints(dependencyId)` (safe: the base algorithm reconciles in topological order and gates each resource before the next). On Kubernetes, inject the stable Service DNS name — never a NodePort/Ingress allocation.
6. **Environment-variable configuration bridge.** Resource manifests and runtimes agree on `COHESION_*` environment variable names by convention, without sharing an assembly. Caller-supplied variables always win over convention-injected ones; platform-allocated values (e.g., ports) are injected by the gateway only when the manifest left them unset.
7. **Readiness is platform-native.** Prefer the platform's own readiness signal (Kubernetes readiness derivation from `observedGeneration`/`readyReplicas`, container health state) over stdout-marker heuristics. Stdout markers are a LocalGateway MVP mechanism, not a platform pattern.
8. **Failure semantics come from the base.** A resource that does not reach `Running` within the readiness budget marks transitive dependents `Blocked` and aborts startup with best-effort reverse teardown. Do not re-implement or bypass this in platform code.
9. **Teardown is reverse-order and best-effort.** `DeleteAsync` failures are swallowed by the base; platform teardown (e.g., namespace deletion) must be idempotent.

## Area layout

- `platforms/Containers/` — shared container-gateway infrastructure used by both Docker and Kubernetes: the container artifact + image index (`application.images.json`) model, OCI tarball store, the embedded OCI Distribution registry, and shared test primitives. Platform gateways depend on `Containers` libraries; `Containers` libraries never depend on a specific platform gateway.
- `platforms/Kubernetes/`, `platforms/Docker/` — one area per target platform.
- Cross-area references flow **only** toward `Containers`; Kubernetes and Docker areas never reference each other.

## AOT posture

**No hard AOT mandate in this repo** (owner decision, 2026-07-20): gateways are deploy-time control planes, so `KubernetesClient` and similar dependencies need no exception machinery. The cohesion repo's libraries — the deployed runtimes — keep the hard `IsAotCompatible` requirement, and any code in this repo that ships *inside* deployed workloads (the sample resource runtime) follows that cohesion posture. Keep source-generated serialization as the default for startup/perf hygiene. Reintroducing a repo-wide mandate requires explicit owner confirmation (see `deviations.md`).

## Relaxing these rules

Changing a hard rule means updating this file, the affected area `README.md`, and (where enforced) `build/Targets/Build.Rules.targets` in the same commit, with explicit user confirmation — mirror of the cohesion repo's process.
