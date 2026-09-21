# Containers (platform area)

Shared, platform-neutral container-gateway infrastructure used by both the Docker and Kubernetes
gateways. This area exists so the two platform gateways stay thin: everything about *images* —
locating them, verifying them, indexing them, and serving them — lives here.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` | `cohesion/image/v1` resource-index and `cohesion/images/v1` application-index readers, digest-pinned artifact seam, verified OCI/docker-save disk store, pull-only embedded OCI Distribution registry, and shared acquisition primitives. Platform gateways use cohesion's public `InMemoryResourceStateManager`; this package carries no duplicate state manager. |

Design item 35 adds the exact [`image.json` and `application.images.json` contract](Assimalign.Cohesion.ApplicationModel.Gateway.Containers/docs/IMAGE_INDEX.md).
Each plan resolves only its own entry (`ArtifactRef.Self`); missing entries, duplicate resources,
tag-only identities, and digest mismatches fail before platform contact. Every entry has an
authority-free repository, immutable digest, and lowercase OCI platform. An omitted or null
registry is late-bound; a concrete registry authority is pinned and cannot be replaced by a
target override. `archive` is optional, relative to the index, cannot escape its directory, and
is omitted when no archive is available.

The on-disk store verifies every OCI blob before placing it under its SHA-256 address. A bounded
BCL loopback HTTP/1.1 listener serves stored manifests and repository-reachable blobs through the
pull-only Registry API v2 (`GET`/`HEAD`). This BCL implementation preserves COHPLT001: the older
program-plan suggestion to use `Web.Routing` would transitively bring the forbidden
`Assimalign.Cohesion.Hosting` assembly into this shipped project.

The cohesion SDK produces and gathers the documented indexes. Kind registry provisioning and
node routing belong to the Kubernetes area; this area owns the shared push protocol.

## Layering

- This repo realizes Cohesion's L2 application model onto deployment targets; Containers is the
  shared substrate of that realization.
- Depends on the generic `Assimalign.Cohesion.ApplicationModel` contract and `.Gateway` base
  (NuGet, from the cohesion repo). It never references an `<Area>.ApplicationModel`, `*.Hosting`,
  an `<Area>.Application` runtime, or `Microsoft.Extensions.*`.
- Platform gateways (`platforms/Kubernetes`, `platforms/Docker`) depend on this area — never the
  reverse, and the two platform areas never reference each other.

See `.claude/rules/platform-areas.md` for the binding architecture rules.

## Local image preparation and registry push

ContainerImagePublishing maps target architecture, invokes CohesionPublishImages through
IContainerImagePublisher, and reloads the resulting index. It never reimplements SDK freshness.
MsBuildContainerImagePublisher uses argument-list process invocation and kills its child tree on
cancellation. Explicit indexes bypass both publishing and target inspection. Source manifests
resolve their own index entry without requiring artifact.image; package identity is still checked.

OciRegistryPush uploads a verified OCI store closure through Registry API v2. It rechecks hashes,
HEADs blobs, uploads missing content, PUTs the manifest by its unchanged digest, and verifies digest
acknowledgements. Upload locations must stay on the selected registry authority. The client reads
internal OciImageStore content directly within this assembly; IOciImageStore was not widened.
The embedded registry remains pull-only and loopback-bound. Kind uses a provisioned registry
container for node reachability. See the [Distribution API](https://distribution.github.io/distribution/spec/api/).
