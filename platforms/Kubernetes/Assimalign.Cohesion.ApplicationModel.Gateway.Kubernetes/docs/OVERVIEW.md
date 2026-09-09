# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Overview

The Kubernetes platform gateway: `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`)
compiles each validated `ResourcePlan` into Kubernetes objects and reconciles them onto a cluster.

<!-- Deviates from the prior Kubernetes stop/teardown and multi-cluster non-goal text per
Developer-Experience Design item 33 and §12 deviations (10)–(11), owner-approved 2026-09-06. -->

**Planned scope (after design item 33's package-contract gate):**

- `KubernetesPlanCompiler` — one pure compiler for `cohesion/plan/v1`, never keyed on kind, area,
  or CLR type. It produces ConfigMap/Secret/PVC plus Deployment, StatefulSet, DaemonSet, or Job,
  Services, exposures, probes, runtime-contract values, and plan hash.
- `KubernetesPlanController` — idempotent server-side apply and best-effort reverse deletion.
  Registered domain overrides from `ApplicationGatewayOptions.Controllers` are consulted before
  this built-in controller.
- One list+watch informer — per-workload readiness, `410 Gone` re-list, liveness-driven
  `Degraded`, and observed Service-DNS endpoint publication through the public
  `InMemoryResourceStateManager`.
- Kind-on-Podman image loading, ownership/adoption, bootstrap output, render mode, and E2E coverage.

**Dependencies:** the generic ApplicationModel + Gateway base packages, `platforms/Containers`,
and `KubernetesClient`. COHPLT001 forbids resource-area `.ApplicationModel`, `*.Hosting`,
`.Application` runtime, and `Microsoft.Extensions.*` assemblies from the resolved closure.

**Lifecycle:** `StopAsync` leaves persistent cluster state in place; `UninstallAsync`
(`--mode teardown`) removes managed objects and the namespace in best-effort reverse order.

**AOT:** no mandate in this repo (owner decision, 2026-07-20); see `docs/DESIGN.md`.

**Status:** the connection/options/namespace skeleton has landed. It predates the realization-plan
contract and still couples namespace deletion to observer stop. The compiler/controller item that
follows this re-pin replaces that behavior; it is not implemented by design item 33.
