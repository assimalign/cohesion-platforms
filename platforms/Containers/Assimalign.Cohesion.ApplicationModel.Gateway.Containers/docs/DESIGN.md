# Assimalign.Cohesion.ApplicationModel.Gateway.Containers — Design

> Implementation record for developer-experience design item 35. The authoritative upstream direction is
> `docs/DEVELOPER_EXPERIENCE_DESIGN.md` and the plan contract is
> `docs/REALIZATION_PLAN.md` in the cohesion repo.

## Design intent

Docker and Kubernetes gateways share one problem that has nothing to do with either platform:
preparing source application images through the SDK and turning digest-pinned images into
gathered artifacts a gateway can realize.
This package owns that problem — the artifact/index model, digest verification, the OCI store —
so each platform gateway supplies only its reconcile/observe specifics.

## Commitments

- **Digest-pinned always.** An image reference is `{repository}@sha256:{digest}`; tags exist only
  as human-readable metadata. Rejected alternative: tag-based deploys — mutable tags break the
  desired-state model's immutability guarantee (upstream decision, inherited).
- **Use the public reference state manager.** Platform gateways consume
  `InMemoryResourceStateManager` from `Assimalign.Cohesion.ApplicationModel.Gateway`. Rejected
  alternative: retaining a Containers-owned copy — duplicate lifecycle behavior would drift from
  the gateway contract and force this repo to repeat upstream's state-manager test matrix.
- **Local preparation delegates to the SDK.** The owner decision of 2026-09-21 replaces the
  prebuilt-only Local loop: a container gateway invokes CohesionPublishImages before resolving
  artifacts, while the SDK owns fingerprint freshness. Explicit indexes bypass publishing.
  The IContainerImagePublisher seam permits command testing without starting MSBuild. Its default
  invoker uses dotnet msbuild with -restore and a target RID; it never publishes the running
  apphost output. Streams are relayed explicitly and cancellation kills the child process tree.
- **No compiler here.** Docker and Kubernetes each own exactly one compiler for `ResourcePlan`;
  shared Containers code owns only artifact/image/registry mechanics and test primitives. A shared
  compiler would erase platform-specific validation and object construction boundaries.
- **Two exact index schemas, one entry contract.** Per-resource `image.json` uses
  `cohesion/image/v1`; application `application.images.json` uses `cohesion/images/v1`, and its
  entries use the same image fields without repeating `schema`. Unknown properties and incomplete
  entries fail. Every repository is authority-free; an omitted or null registry is late-bound,
  while a concrete registry authority is pinned and cannot be replaced by a target override. The
  OCI platform is required. Application entries are unique by resource and resolution accepts
  only the caller's own entry for `ArtifactRef.Self`. Optional `archive` is document-relative,
  contained, and omitted when unavailable. The complete producer contract is
  [IMAGE_INDEX.md](IMAGE_INDEX.md).
- **Verify before address.** OCI source bytes are hashed while they are copied to a temporary
  file, then atomically committed beneath `blobs/sha256` only when their advertised digest
  matches. The repository manifest link records the reachable config/layer closure. Repeated and
  concurrent ingestion is idempotent; an existing corrupt blob is an error rather than trusted.
- **Preserve an honest Docker-save boundary.** Docker-save archives have no portable copy of the
  original registry manifest. The store hashes the config/layers and deterministically constructs
  the Docker schema-2 manifest; ingestion succeeds only when that reconstructed manifest equals
  the index digest. It never associates caller-supplied digest metadata with different bytes.
- **Pull-only embedded registry.** A loopback BCL HTTP/1.1 listener implements only `/v2/` and
  manifest/blob `GET`/`HEAD` by digest. Tags, catalog, upload, deletion, and authentication are not
  implemented. Repository-scoped closure checks prevent one repository name from exposing an
  unrelated CAS blob.
- **BCL transport preserves platform layering.** The earlier program plan proposed Cohesion's Web
  routing stack. Its published closure currently includes `Assimalign.Cohesion.Hosting`, which is
  forbidden in shipped platform projects by COHPLT001. The TCP listener caps active connections
  and its accept backlog at 64, limits headers to 32 KiB, and closes connections whose request
  headers do not complete within 10 seconds. It follows the Docker Engine client's BCL precedent
  without adding `Microsoft.Extensions.*` or a Hosting seam.

## AOT posture

No mandate (repo-wide owner decision, 2026-07-20 — see `.claude/rules/general-rules.md § AOT
posture`); serialization stays source-generated (`System.Text.Json` source-gen for the image
index) for startup/perf hygiene, and the sample resource runtime — which models a deployed
cohesion service — keeps the cohesion AOT posture.

## Acquisition boundary

`ContainerImageIndexes` resolves and validates metadata; `OciImageStores` ingests bytes;
`EmbeddedOciRegistries` serves verified bytes. Docker uses the archive path for verified
load/run-by-ID and pulls registry-backed images through the Engine API by digest. Kubernetes pushes
verified archives to the provisioned Local Kind registry or returns a registry-resolved digest, preserving
the entry's pinned authority or applying a target authority only when `registry` is omitted or
null.

The embedded listener is deliberately loopback-only. Kind uses a separate registry container reachable from the node network; this listener
remains useful for loopback consumers. Arbitrary cluster topology is outside this package.

## Non-goals

- No platform API clients (Kubernetes/Docker specifics live in their areas).
- No image builder implementation or tag resolution against remote registries; building remains an SDK target.
- No registry authentication, garbage collection, or Kubernetes reachability provisioning.

## Publication identity and delivery

The source application's own manifest supplies artifact.project. Models in the current contract
carry member manifests only, so the resolver falls back to the entry assembly's embedded
cohesion/resource.json, checking its application and gateway identity. This is a local compatibility
path; it does not widen IApplicationModel, add a project-path switch, or scan assemblies.

The push client stays beside OciImageStore and reads its existing internal verified-content
methods. IOciImageStore remains an ingestion contract. Reusing the internal content reader avoids
exposing filesystem/blob state through that public interface. Each push rechecks hashes, uploads
only missing blobs, sends the original manifest by digest, and requires matching acknowledgements.
Upload Location remains within the registry authority; authentication and redirect-based storage
services are outside the local-registry protocol scope. Kind's container network is provisioned by
the Kubernetes area rather than by changing the embedded registry's loopback binding.
