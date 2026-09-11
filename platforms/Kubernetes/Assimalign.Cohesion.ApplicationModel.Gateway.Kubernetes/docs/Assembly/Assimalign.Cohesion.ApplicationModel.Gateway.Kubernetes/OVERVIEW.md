# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes

Assembly: `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`

Realizes validated Cohesion resource plans as Kubernetes objects, reconciles their lifecycle, and
observes workload state through the Kubernetes API.

Design item 35 extends [`KubernetesGatewayOptions`](KubernetesGatewayOptions/OVERVIEW.md) with a
validated application image-index path and registry resolution. During Gather, the gateway
resolves only a resource's own `ArtifactRef.Self`, keeps every reference digest-pinned, loads an
advertised archive into a Development Kind cluster, preserves a concrete pinned entry registry,
or applies a target registry only when the entry registry is omitted or null.

See the project [overview](../../OVERVIEW.md) and [design](../../DESIGN.md) for the compiler,
controller, observer, lifecycle, and extension contracts.
