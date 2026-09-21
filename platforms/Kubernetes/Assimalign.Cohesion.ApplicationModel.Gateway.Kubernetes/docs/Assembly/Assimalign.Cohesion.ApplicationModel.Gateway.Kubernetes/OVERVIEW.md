# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes

Assembly: `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`

Realizes validated Cohesion resource plans as Kubernetes objects, reconciles their lifecycle, and
observes workload state through the Kubernetes API.

Design item 35 extends [`KubernetesGatewayOptions`](KubernetesGatewayOptions/OVERVIEW.md) with a
validated application image-index path and registry resolution. During Gather, the gateway
resolves only a resource's own `ArtifactRef.Self`, keeps every reference digest-pinned, pushes a
verified archive to the Local Kind registry, preserves a concrete pinned entry registry,
or applies a target registry only when the entry registry is omitted or null.

See the project [overview](../../OVERVIEW.md) and [design](../../DESIGN.md) for the compiler,
controller, observer, lifecycle, and extension contracts.

Item 37 adds KubernetesGatewayCommandLine, KubernetesSystemExposure, and
IKubernetesApplicationModelResolver; their type pages describe the SDK hook, external control-plane
options, and operator-channel URL import. KubernetesGateway implements the upstream render and
bootstrap interfaces. Installation and discovery are separate from application plan compilation.
[`KubernetesKindCluster`](KubernetesKindCluster/OVERVIEW.md) provisions or reuses the Podman registry
and Kind node route. Local source publication uses the target architecture; an explicit index
bypasses the SDK invocation.
