# Assimalign.Cohesion.ApplicationModel.Gateway.Containers — Overview

Shared container-gateway infrastructure for Cohesion platform gateways (Docker, Kubernetes).

**Scope (per the [program plan](../../../../docs/PLATFORMS_PROGRAM_PLAN.md)):**

- `ContainerImageArtifact` — the `IContainerImageArtifact` implementation gateways return from
  `GatherAsync` (digest-pinned; a tag is never a pull reference). ([#8](https://github.com/assimalign/cohesion-platforms/issues/8))
- Strict source-generated readers for per-resource `image.json` and gateway-level
  `application.images.json`, both versioned `cohesion/images/v1`. Entries carry resource,
  repository, SHA-256 digest, optional tag/archive path, AOT and base-image facts, and an explicit
  null/late-bound registry value. See [IMAGE_INDEX.md](IMAGE_INDEX.md).
- Shared controller/compiler test primitives. State storage is supplied by the public
  `InMemoryResourceStateManager` in `Assimalign.Cohesion.ApplicationModel.Gateway`; the retired
  local `GatewayResourceStateManager` is not part of this package.
- The OCI image store: ingest OCI image-layout directories/tarballs and Docker-save tarballs into
  a content-addressed blob/manifest store, verifying bytes before committing their digest and
  scoping served blobs to a repository's manifest closure.
- A pull-only embedded OCI Distribution registry on loopback. It serves `/v2/`, manifests, and
  blobs by digest with `GET`, `HEAD`, exact content metadata, and registry-shaped 404 responses.
  It uses BCL TCP/HTTP primitives so the package remains inside COHPLT001's allowed closure.

**Dependencies:** `Assimalign.Cohesion.ApplicationModel`, `Assimalign.Cohesion.ApplicationModel.Gateway`
(NuGet, cohesion repo). No platform client libraries — platform gateways depend on this package,
never the reverse.

**Status:** design item 35 supplies the shared image-index, archive/store, and embedded-registry
foundation. The upstream Cohesion SDK remains the producer of `image.json` and
`application.images.json`; Kubernetes node-reachable registry topologies remain
`L04.01.03.08` / #22.
