# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Design

> Scaffold-stage document: records the signed direction this package is being built toward. The
> authority is `docs/DEVELOPER_EXPERIENCE_DESIGN.md` (signed 2026-09-06) and
> `docs/REALIZATION_PLAN.md` in the cohesion repo.

<!-- Deviates from the prior Kubernetes stop/teardown and multi-cluster non-goal text per
Developer-Experience Design item 33 and §12 deviations (10)–(11), owner-approved 2026-09-06. -->

## Design intent

`KubernetesGateway` derives from the guided `ApplicationGateway` base and supplies Kubernetes
platform hooks. Exactly one pure `KubernetesPlanCompiler` translates a validated `ResourcePlan`
into platform objects; one level-triggered `KubernetesPlanController` applies them idempotently;
one informer observes the result. The base owns ordering, plan-derived readiness gates, blocked
propagation, startup rollback, non-destructive stop, and destructive uninstall.

## Commitments

- **One compiler, independent of resource identity.** The compiler consumes
  `cohesion/plan/v1` without dispatching on manifest kind, resource area, or CLR type. Unknown
  specification fields, enum values, and unsupported schemas are errors before gathering or
  cluster contact. Unknown hints warn once and are ignored.
- **Plan-shaped output.** Workload kind maps directly to Deployment, StatefulSet, DaemonSet, or
  Job. The compiler also emits the plan's ConfigMaps/Secrets, Services, claims/PVC templates,
  exposures, probes, runtime-contract values, and stop grace. Images remain digest-pinned.
- **Observed state has one Kubernetes writer.** The single list+watch informer publishes locally
  realized lifecycle and `ResourceEndpoint` observations through cohesion's public
  `InMemoryResourceStateManager`. A failing liveness signal can observe `Degraded`; it is never a
  readiness terminal and never re-gates an admitted dependent. Watches re-list on `410 Gone`.
- **Plan-derived readiness.** Workload/pod status and plan probes are combined into the exact
  `ReadinessGate` carried by the plan. Long-running workloads gate on
  `{Running, Failed, Stopped}` and satisfy only on `Running`; Jobs satisfy on `Stopped`.
- **Service DNS is discovery.** Observed dependency snapshots project stable Service DNS to the
  canonical `System.Uri` runtime contract. A StatefulSet uses its governing Service. In-cluster
  dependencies never receive a NodePort or Ingress allocation.
- **Owned namespaces.** Each application namespace carries `cohesion.io/owner`; a foreign owner
  is refused unless explicitly adopted. The field manager is the gateway identity.

## Why-this-not-that decisions

- **One plan controller, not per-resource controllers.** Domain-authored overrides registered in
  `ApplicationGatewayOptions.Controllers` are consulted first, then the base external controller,
  then the built-in Kubernetes plan controller. The first `CanRealize(plan, out reason)` match
  wins. Rejected alternative: controllers keyed on kind or CLR capability interfaces — it creates
  a platform × resource-area matrix and makes new plan fields silently disappear.
- **Stop is not teardown.** `StopAsync` releases observation and runtime supervision while leaving
  Kubernetes objects and persistent state intact. `UninstallAsync` (`--mode teardown`) deletes
  resources and the namespace in best-effort reverse order. Stop and uninstall attempt every
  remaining resource and report the first failure afterward; startup rollback preserves its
  original failure while suppressing cleanup failures.
- **`KUBECONFIG` is a path list.** Resolution mirrors kubectl: explicit `KubeConfigPath` →
  `KUBECONFIG` (first existing entry in the platform path-separated list) → default
  `~/.kube/config` → in-cluster configuration. Failure is actionable.

## AOT posture

No mandate: gateways are deploy-time control planes (owner decision, 2026-07-20).
`KubernetesClient` therefore requires no AOT exception machinery. Source-generated serialization
remains preferred for dependency and startup hygiene.

## Non-goals

- Helm and kustomize are not the desired-state source. `--mode render` does emit the compiled
  Kubernetes objects without contacting a cluster for review and policy checks.
- No single gateway reconciles multiple clusters. Federation is composed from one gateway per
  cluster, peer control planes/exported models, external references, and `IApplicationSet`.

## Current delivery boundary

The checked-in gateway is the pre-plan skeleton: it proves cluster configuration and namespace
connectivity. It still co-locates namespace ensure/delete with observer hooks. The following
Kubernetes compiler/controller work replaces that skeleton and implements the stop/teardown split;
design item 33 deliberately does not begin that implementation.
