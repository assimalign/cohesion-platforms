# Assimalign.Cohesion.ApplicationModel.Gateway.Containers

Assembly: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

Shared, platform-neutral image acquisition contracts for Cohesion gateways. The assembly reads
strict image indexes, creates digest-pinned artifacts, ingests OCI or Docker-save content into a
verified store, and exposes that store through an optional pull-only loopback registry.

## Public API

| Type | Purpose |
| --- | --- |
| [`ContainerImageArtifacts`](ContainerImageArtifacts/OVERVIEW.md) | Creates immutable digest-pinned image artifacts. |
| [`ContainerImageIndexes`](ContainerImageIndexes/OVERVIEW.md) | Reads, validates, resolves, and registry-binds `cohesion/images/v1` indexes. |
| [`IApplicationImageIndex`](IApplicationImageIndex/OVERVIEW.md) | Represents one application's ordered resource-image entries. |
| [`IContainerImageIndexEntry`](IContainerImageIndexEntry/OVERVIEW.md) | Represents one resource's validated image metadata. |
| [`OciImageStores`](OciImageStores/OVERVIEW.md) | Creates content-addressed OCI image stores. |
| [`IOciImageStore`](IOciImageStore/OVERVIEW.md) | Ingests and verifies image layouts and archives. |
| [`EmbeddedOciRegistries`](EmbeddedOciRegistries/OVERVIEW.md) | Creates pull-only loopback registries. |
| [`IEmbeddedOciRegistry`](IEmbeddedOciRegistry/OVERVIEW.md) | Controls the loopback Registry HTTP API v2 endpoint. |

Both index forms and their validation rules are defined by the
[image-index schema](../../IMAGE_INDEX.md). Implementations are internal and are obtained only
through the public factories.
