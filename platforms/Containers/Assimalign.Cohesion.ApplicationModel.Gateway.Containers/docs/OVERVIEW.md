# Assimalign.Cohesion.ApplicationModel.Gateway.Containers — Overview

Shared container-gateway infrastructure for Cohesion platform gateways (Docker, Kubernetes).

**Scope (per the [program plan](../../../../docs/PLATFORMS_PROGRAM_PLAN.md)):**

- `ContainerImageArtifact` — the `IContainerImageArtifact` implementation gateways return from
  `GatherAsync` (digest-pinned; a tag is never a pull reference). ([#8](https://github.com/assimalign/cohesion-platforms/issues/8))
- The `application.images.json` image index (ResourceName → repository/digest/tag/archive path)
  with an AOT-safe source-generated serializer. ([#8](https://github.com/assimalign/cohesion-platforms/issues/8))
- A public `IApplicationResourceStateManager` implementation matching the upstream reference
  semantics (the upstream one is `internal`). ([#9](https://github.com/assimalign/cohesion-platforms/issues/9))
- The OCI image store: unpack OCI image-layout tarballs into a content-addressed, digest-verified
  blob/manifest store. ([#11](https://github.com/assimalign/cohesion-platforms/issues/11))
- Shared test primitives for controller/gateway tests.

**Dependencies:** `Assimalign.Cohesion.ApplicationModel`, `Assimalign.Cohesion.ApplicationModel.Gateway`
(NuGet, cohesion repo). No platform client libraries — platform gateways depend on this package,
never the reverse.

**Status:** scaffolded; no implementation yet.
