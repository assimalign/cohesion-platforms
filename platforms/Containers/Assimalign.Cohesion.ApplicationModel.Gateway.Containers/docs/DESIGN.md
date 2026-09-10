# Assimalign.Cohesion.ApplicationModel.Gateway.Containers — Design

> Scaffold-stage document: records the design intent this package is being built to; sections grow
> as features land. The authoritative upstream direction is
> `docs/DEVELOPER_EXPERIENCE_DESIGN.md` and the plan contract is
> `docs/REALIZATION_PLAN.md` in the cohesion repo.

## Design intent

Docker and Kubernetes gateways share one problem that has nothing to do with either platform:
turning *pre-built, digest-pinned container images* into gathered artifacts a gateway can realize.
This package owns that problem — the artifact/index model, digest verification, the OCI store —
so each platform gateway supplies only its reconcile/observe specifics.

## Why-this-not-that (initial commitments)

- **Digest-pinned always.** An image reference is `{repository}@sha256:{digest}`; tags exist only
  as human-readable metadata. Rejected alternative: tag-based deploys — mutable tags break the
  desired-state model's immutability guarantee (upstream decision, inherited).
- **Use the public reference state manager.** Platform gateways consume
  `InMemoryResourceStateManager` from `Assimalign.Cohesion.ApplicationModel.Gateway`. Rejected
  alternative: retaining a Containers-owned copy — duplicate lifecycle behavior would drift from
  the gateway contract and force this repo to repeat upstream's state-manager test matrix.
- **Gather, never build.** `GatherAsync` locates/validates artifacts produced upstream by
  `PublishContainer`. Rejected alternative: building images inside the gateway — collapses the
  build/run boundary the upstream design draws deliberately.
- **No compiler here.** Docker and Kubernetes each own exactly one compiler for `ResourcePlan`;
  shared Containers code owns only artifact/image/registry mechanics and test primitives. A shared
  compiler would erase platform-specific validation and object construction boundaries.

## AOT posture

No mandate (repo-wide owner decision, 2026-07-20 — see `.claude/rules/general-rules.md § AOT
posture`); serialization stays source-generated (`System.Text.Json` source-gen for the image
index) for startup/perf hygiene, and the sample resource runtime — which models a deployed
cohesion service — keeps the cohesion AOT posture.

## Current delivery boundary

Design item 34 supplies `ContainerImageArtifacts.Create` as the small public construction seam
for an already digest-pinned `{repository}@sha256:{digest}` artifact. It performs no lookup,
registry access, archive handling, or tag resolution. Those acquisition/index responsibilities
remain together in design item 35.

## Non-goals

- No platform API clients (Kubernetes/Docker specifics live in their areas).
- No image *building* or tag resolution against remote registries.
