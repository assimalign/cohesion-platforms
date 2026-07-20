# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Design

> Scaffold-stage document: records the design intent this package is being built to; sections grow
> as features land. The authoritative upstream design is `libraries/ApplicationModel/DESIGN.md`
> (cohesion repo, v2.x) — §7–8 specify this gateway; nothing here may contradict it.

## Design intent

Subclass the upstream guided base `ApplicationGateway` and supply only Kubernetes specifics:
controllers that server-side-apply desired state, a single informer that owns observed state, and
image reachability for the cluster at hand. The base owns the algorithm (topological gather →
observe → reconcile with readiness gates → Blocked propagation → reverse teardown); this package
must never re-implement or bypass it.

## Initial commitments (from the upstream design)

- **Namespace = `ApplicationName`.** The application's namespace is its unit of ownership and of
  teardown (`StopAsync` deletes it).
- **Server-side apply with a fixed field manager** for every object; the informer ignores the
  gateway's own writes.
- **Readiness derivation:** `Running` ⇔ `observedGeneration == generation && updatedReplicas ==
  readyReplicas == spec.replicas`; watch re-lists on `410 Gone`.
- **Digest-pinned images only**; dev image path is Kind daemon-load, production paths are registry
  topologies selected via `IApplicationEnvironment`.
- **Service DNS for discovery** — dependents get stable Service DNS via observed endpoints, never
  NodePort/Ingress allocations.

## Why-this-not-that decisions

- **Namespace lifecycle is gateway-level, not a resource controller.** The upstream base routes
  each resource to exactly ONE controller (first `CanControl` match wins), so a namespace
  "controller" matching every resource would shadow the workload controller. The namespace is
  per-*application*, not per-resource: it is ensured in `StartObserverAsync` (before any
  provisioning) and deleted best-effort in `StopObserverAsync` (after all resource teardown),
  via the internal `KubernetesNamespaceManager`. Rejected alternative: an
  `IApplicationResourceController` for namespaces — incompatible with first-match-wins routing.
- **Teardown never throws.** Namespace deletion tolerates 404 and swallows all other failures
  (RBAC, conflicts, unreachable cluster) bounded by `StopGrace` — a leftover namespace is
  re-ensured idempotently on the next start. Rejected alternative: surfacing delete failures —
  would turn best-effort teardown into a hard failure path the base contract forbids.
- **`KUBECONFIG` is a path list.** Resolution mirrors kubectl: explicit `KubeConfigPath` →
  `KUBECONFIG` (first existing file of the `Path.PathSeparator`-separated list) → default
  `~/.kube/config` → in-cluster config; anything else fails with an actionable error.

## AOT posture

No mandate: the repo-wide AOT requirement was dropped by owner decision on 2026-07-20 (gateways
are deploy-time control planes; the cohesion libraries — the deployed runtimes — keep the hard
requirement). `KubernetesClient` therefore needs no exception machinery, and the formerly gating
AOT spike ([#15](https://github.com/assimalign/cohesion-platforms/issues/15)) was closed as
obsolete. A hand-rolled typed REST client over Cohesion's own HTTP stack remains a
dependency-hygiene option, not an AOT necessity. Restore currently flags GHSA-w7r3-mgwf-4mqq
(moderate) on `KubernetesClient` 17.0.4 — version bump to be coordinated with the cohesion
central pin.

## Non-goals

- No Helm/kustomize rendering; the application model is the manifest source.
- No multi-cluster orchestration in this program.
