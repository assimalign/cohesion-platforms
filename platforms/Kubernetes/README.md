# Kubernetes (platform area)

The Kubernetes implementation of Cohesion's application-model gateway. One pure
`KubernetesPlanCompiler` translates each validated `ResourcePlan` into Kubernetes objects, and one
`KubernetesPlanController` applies them idempotently. A single list+watch informer is the sole
writer of observed state for locally realized Kubernetes resources.

<!-- Deviates from the prior Kubernetes stop/teardown and multi-cluster non-goal text per
Developer-Experience Design item 33 and §12 deviations (10)–(11), owner-approved 2026-09-06. -->

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` | `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`), `KubernetesPlanCompiler`, plan controller, informer/observer, `UseKubernetesGateway()`. The compiler/controller work follows design item 33's re-pin. |

## Layering & posture

- Depends on the generic ApplicationModel contract + Gateway base (NuGet),
  `platforms/Containers`, and `KubernetesClient` (centrally pinned). It never references a
  resource area's `.ApplicationModel`, `*.Hosting`, `.Application` runtime, or
  `Microsoft.Extensions.*` assembly.
- **AOT:** this repo carries no `IsAotCompatible` mandate (owner decision, 2026-07-20) — gateways
  are deploy-time control planes, so `KubernetesClient` needs no exception machinery. A typed REST
  client on Cohesion's own HTTP stack remains a dependency-hygiene option, not an AOT necessity.
- Development target: **Kind on Podman** (`KIND_EXPERIMENTAL_PROVIDER=podman`), daemon-load image
  path first; registry topologies later ([#22](https://github.com/assimalign/cohesion-platforms/issues/22)).
- Plan probes combine with workload/pod status to produce the plan-derived readiness gate.
  Probe-driven `Degraded` is observed after readiness and never re-gates dependents. Observed
  dependency addresses use stable Kubernetes Service DNS.

## Lifecycle and non-goals

- `StopAsync` releases observation and runtime supervision in best-effort reverse order while
  leaving persistent cluster state in place. `UninstallAsync` (`--mode teardown`) removes managed
  objects and the application namespace in best-effort reverse order; deletion is idempotent and
  later deletions are still attempted after a failure.
- One gateway never reconciles multiple clusters. Federation uses one gateway per cluster plus
  peer control planes, exported models, external references, and `IApplicationSet`; cross-cluster
  references are supported without turning one gateway into a multi-cluster reconciler.

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing.
