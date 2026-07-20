# Assimalign.Cohesion.ApplicationModel.Gateway.Containers — Design

> Scaffold-stage document: records the design intent this package is being built to; sections grow
> as features land. The authoritative upstream design is `libraries/ApplicationModel/DESIGN.md`
> (cohesion repo, v2.x); nothing here may contradict it.

## Design intent

Docker and Kubernetes gateways share one problem that has nothing to do with either platform:
turning *pre-built, digest-pinned container images* into gathered artifacts a gateway can realize.
This package owns that problem — the artifact/index model, digest verification, the OCI store —
so each platform gateway supplies only its reconcile/observe specifics.

## Why-this-not-that (initial commitments)

- **Digest-pinned always.** An image reference is `{repository}@sha256:{digest}`; tags exist only
  as human-readable metadata. Rejected alternative: tag-based deploys — mutable tags break the
  desired-state model's immutability guarantee (upstream decision, inherited).
- **A public state manager rather than upstream internals.** The Gateway package's reference
  `InMemoryResourceStateManager` is `internal`. We re-implement its documented contract publicly
  (one lock; waiter registration under the lock; events/waiters completed outside; terminal-set
  waits; timeout returns last observed state) instead of using `InternalsVisibleTo`, and propose
  upstreaming the public type. Rejected alternative: asking cohesion for `InternalsVisibleTo` —
  couples two repos' assembly identities for a contract that is deliberately public.
- **Gather, never build.** `GatherAsync` locates/validates artifacts produced upstream by
  `PublishContainer`. Rejected alternative: building images inside the gateway — collapses the
  build/run boundary the upstream design draws deliberately.

## AOT posture

`IsAotCompatible=true`; serialization is source-generated (`System.Text.Json` source-gen for the
image index). No reflection.

## Non-goals

- No platform API clients (Kubernetes/Docker specifics live in their areas).
- No image *building* or tag resolution against remote registries.
