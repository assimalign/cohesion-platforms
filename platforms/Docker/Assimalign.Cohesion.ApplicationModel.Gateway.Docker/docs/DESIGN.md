# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Design

> Scaffold-stage document: records the design intent this package is being built to; sections grow
> as features land. The authoritative upstream design is `libraries/ApplicationModel/DESIGN.md`
> (cohesion repo, v2.x); nothing here may contradict it.

## Design intent

The smaller container-gateway sibling: same gathered digest-pinned images, same base algorithm,
with the engine's containers/networks as the realization target. Everything image-shaped comes
from `platforms/Containers`; this package owns only the engine client, the container controller,
and the event observer.

## Why-this-not-that (initial commitments)

- **Hand-rolled engine client, not Docker.DotNet.** Docker.DotNet's reflection-based serialization
  violates the repo AOT mandate; the gateway needs a small, fixed API surface (images, containers,
  networks, events), which a typed client over Cohesion's own connection stack covers AOT-cleanly.
- **Ownership labels over name conventions.** Containers/networks carry `cohesion.application` /
  `cohesion.resource` labels so reconcile and teardown identify owned objects robustly; names stay
  human-friendly but are not the identity.
- **Events + inspect, not polling alone.** The events stream drives responsiveness; periodic
  inspect re-sync keeps the state level-true across stream drops (mirror of the informer re-list
  rule on Kubernetes).

## AOT posture

`IsAotCompatible=true` — no exception here; the client is built for it.

## Non-goals

- No Docker Compose interop; the application model is the composition source.
- No Swarm; single-engine scope for this program.
