# Containers (platform area)

Shared, platform-neutral container-gateway infrastructure used by both the Docker and Kubernetes
gateways. This area exists so the two platform gateways stay thin: everything about *images* —
locating them, verifying them, indexing them, and serving them — lives here.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` | `cohesion/images/v1` index reader, digest-pinned artifact seam, verified OCI/docker-save disk store, pull-only embedded OCI Distribution registry, and shared acquisition primitives. Platform gateways use cohesion's public `InMemoryResourceStateManager`; this package carries no duplicate state manager. |

Design item 35 adds the exact [`image.json` and `application.images.json` contract](Assimalign.Cohesion.ApplicationModel.Gateway.Containers/docs/IMAGE_INDEX.md).
Each plan resolves only its own entry (`ArtifactRef.Self`); missing entries, duplicate resources,
tag-only identities, and digest mismatches fail before platform contact. `archivePath` is optional,
relative to the index, and cannot escape its directory.

The on-disk store verifies every OCI blob before placing it under its SHA-256 address. A bounded
BCL loopback HTTP/1.1 listener serves stored manifests and repository-reachable blobs through the
pull-only Registry API v2 (`GET`/`HEAD`). This BCL implementation preserves COHPLT001: the older
program-plan suggestion to use `Web.Routing` would transitively bring the forbidden
`Assimalign.Cohesion.Hosting` assembly into this shipped project.

The cohesion SDK item will produce and gather the documented indexes. General registry
reachability from Kubernetes nodes remains Kubernetes `L04.01.03.08` / #22; this area does not
invent that topology.

## Layering

- This repo realizes Cohesion's L2 application model onto deployment targets; Containers is the
  shared substrate of that realization.
- Depends on the generic `Assimalign.Cohesion.ApplicationModel` contract and `.Gateway` base
  (NuGet, from the cohesion repo). It never references an `<Area>.ApplicationModel`, `*.Hosting`,
  an `<Area>.Application` runtime, or `Microsoft.Extensions.*`.
- Platform gateways (`platforms/Kubernetes`, `platforms/Docker`) depend on this area — never the
  reverse, and the two platform areas never reference each other.

See `.claude/rules/platform-areas.md` for the binding architecture rules.
