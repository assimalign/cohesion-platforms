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

## AOT posture

`IsAotCompatible=false` — the repo's one sanctioned exception while `KubernetesClient` (not
trim-safe) is the client. The [#15](https://github.com/assimalign/cohesion-platforms/issues/15)
spike records the verdict; the standing fallback is a hand-rolled typed REST client over Cohesion's
own HTTP stack, which would remove the exception. Restore currently flags GHSA-w7r3-mgwf-4mqq
(moderate) on 17.0.4 — version bump to be coordinated with the cohesion central pin.

## Non-goals

- No Helm/kustomize rendering; the application model is the manifest source.
- No multi-cluster orchestration in this program.
