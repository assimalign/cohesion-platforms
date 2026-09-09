# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Design

> Scaffold-stage document: records the design intent this package is being built to; sections grow
> as features land. The authoritative upstream direction is
> `docs/DEVELOPER_EXPERIENCE_DESIGN.md` and the plan contract is
> `docs/REALIZATION_PLAN.md` in the cohesion repo.

## Design intent

The smaller container-gateway sibling: same gathered digest-pinned images and base algorithm, with
the engine's containers/networks as the realization target. One pure `DockerPlanCompiler`
translates every validated `ResourcePlan`; one level-triggered controller applies those operations.
Everything image-shaped comes from `platforms/Containers`; this package owns only compilation,
engine I/O, and event observation.

## Why-this-not-that (initial commitments)

- **Hand-rolled engine client, not Docker.DotNet.** The gateway needs a small, fixed API surface
  (images, containers, networks, events), which a typed client over Cohesion's own connection
  stack covers without pulling in Docker.DotNet's large surface and external serializer
  dependency. (Originally also an AOT concern; the repo's AOT mandate was dropped 2026-07-20, so
  this is now purely a dependency-hygiene choice — revisit if the hand-rolled client grows.)
- **Ownership labels over name conventions.** Containers/networks carry `cohesion.application` /
  `cohesion.resource` labels so reconcile and teardown identify owned objects robustly; names stay
  human-friendly but are not the identity.
- **Events + inspect, not polling alone.** The events stream drives responsiveness; periodic
  inspect re-sync keeps the state level-true across stream drops (mirror of the informer re-list
  rule on Kubernetes).
- **One compiler, not capability dispatch.** The compiler consumes plan specification without
  selecting on resource kind, area, or CLR type. Resolved inputs, artifacts, observed dependency
  snapshots, and platform options are explicit compiler inputs beside the plan. Unknown plan
  specification or schema is rejected; unknown hints warn once and are ignored.
- **Stop is not teardown.** Stop releases supervision and observation while retaining persistent
  engine state. `UninstallAsync` removes containers, volumes selected for teardown, and the
  application network in best-effort reverse order.

## AOT posture

No mandate (repo-wide owner decision, 2026-07-20 — see `.claude/rules/general-rules.md § AOT
posture`); serialization stays source-generated for startup/perf hygiene.

## Non-goals

- No Docker Compose interop; the application model is the composition source.
- No Swarm; single-engine scope for this program.
