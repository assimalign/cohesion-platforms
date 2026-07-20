# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Overview

The Kubernetes platform gateway: `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`)
realizes an `IApplicationModel` onto a cluster.

**Scope (per the [program plan](../../../../docs/PLATFORMS_PROGRAM_PLAN.md)):**

- Gateway skeleton, options (kubeconfig/context/in-cluster), `UseKubernetesGateway()`. ([#16](https://github.com/assimalign/cohesion-platforms/issues/16))
- `KubernetesNamespaceController` — namespace = `model.Name`; teardown deletes it. ([#16](https://github.com/assimalign/cohesion-platforms/issues/16))
- `KubernetesResourceController` — server-side-applies ConfigMap/Deployment/Service, digest-pinned. ([#17](https://github.com/assimalign/cohesion-platforms/issues/17))
- Single list+watch informer: readiness derivation, 410 re-list, observed-endpoint publication. ([#18](https://github.com/assimalign/cohesion-platforms/issues/18))
- Kind-on-Podman daemon-load image path + E2E harness. ([#19](https://github.com/assimalign/cohesion-platforms/issues/19), [#20](https://github.com/assimalign/cohesion-platforms/issues/20))

**Dependencies:** ApplicationModel + Gateway base (NuGet), `platforms/Containers`, `KubernetesClient`.

**AOT:** the repo's one sanctioned `IsAotCompatible` exception (see `docs/DESIGN.md` once the
[#15](https://github.com/assimalign/cohesion-platforms/issues/15) spike lands).

**Status:** in progress — the gateway skeleton
([#16](https://github.com/assimalign/cohesion-platforms/issues/16)) has landed: `KubernetesGateway`,
`KubernetesGatewayOptions`, `UseKubernetesGateway()`, kubeconfig/in-cluster resolution, and the
namespace ensure/delete lifecycle. The workload controller, informer, image path, and E2E harness
are pending ([#17](https://github.com/assimalign/cohesion-platforms/issues/17)–[#20](https://github.com/assimalign/cohesion-platforms/issues/20)).
