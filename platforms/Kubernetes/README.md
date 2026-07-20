# Kubernetes (platform area)

The Kubernetes implementation of Cohesion's application-model gateway: realize an
`IApplicationModel` (an immutable desired-state resource graph) onto a Kubernetes cluster —
namespace per application, digest-pinned server-side-applied workloads, and a single list+watch
informer as the sole writer of observed state.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` | `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`), namespace + resource controllers, informer/observer, `UseKubernetesGateway()`. (Scaffolded — implementation tracked by [#15](https://github.com/assimalign/cohesion-platforms/issues/15)–[#22](https://github.com/assimalign/cohesion-platforms/issues/22).) |

## Layering & posture

- Depends on: ApplicationModel contracts + Gateway base (NuGet), `platforms/Containers`, and
  `KubernetesClient` (centrally pinned).
- **AOT:** this repo carries no `IsAotCompatible` mandate (owner decision, 2026-07-20) — gateways
  are deploy-time control planes, so `KubernetesClient` needs no exception machinery. A typed REST
  client on Cohesion's own HTTP stack remains a dependency-hygiene option, not an AOT necessity.
- Development target: **Kind on Podman** (`KIND_EXPERIMENTAL_PROVIDER=podman`), daemon-load image
  path first; registry topologies later ([#22](https://github.com/assimalign/cohesion-platforms/issues/22)).

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing.
