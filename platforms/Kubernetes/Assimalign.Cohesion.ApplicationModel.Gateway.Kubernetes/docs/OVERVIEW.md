# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Overview

The Kubernetes platform gateway: `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`)
compiles each validated `ResourcePlan` into Kubernetes objects and reconciles them onto a cluster.

<!-- Deviates from the prior Kubernetes stop/teardown and multi-cluster non-goal text per
Developer-Experience Design item 33 and §12 deviations (10)–(11), owner-approved 2026-09-06. -->

**Design item 34 (`L04.01.03.09` / #31) delivery:**

- `KubernetesPlanCompiler` — one pure compiler for `cohesion/plan/v1`, never keyed on manifest
  resource kind, area, or CLR type. It produces ConfigMap/Secret/PVC plus Deployment, StatefulSet, DaemonSet, or Job,
  Services, exposures, probes, runtime-contract values, and plan hash. Resolved mounts,
  credentials, artifact identity, patches, and observed dependencies remain reconciliation inputs
  beside the plan; only the plan contributes to the hash.
- `KubernetesPlanController` — ownership-preflighted, optimistic server-side apply; application-
  and resource-level stale-object pruning; safe replacement of changed immutable Jobs; opaque runtime-input revisions for pod
  rollout; and best-effort reverse deletion. Registered domain overrides from
  `ApplicationGatewayOptions.Controllers` are consulted before this built-in controller.
  Stateful claim-template and patched immutable-controller mutations are refused before writes;
  retained claims must remain storage-compatible, and StatefulSets are controller-only removed or
  recreated under claim-retention-safe policy with no remaining PVC owner reference to the old
  controller. Removed-resource deletion is awaited; PVCs for removed resources are preserved.
- One list+watch observer — an initial labeled list followed from its resource version, plus bounded
  workload/Service/`Endpoints` resync and relisting after expired watches,
  per-workload readiness with generation-current controller failures, liveness-driven `Degraded`, and observed Service-DNS endpoint publication
  through the public `InMemoryResourceStateManager`. Workload revisions exclude old rollout pods
  from readiness and exit-code observations. Per-pod/container restart history survives resync
  without masking pod replacement. Public-address changes update the runtime contract's
  `PUBLIC_URL` through resource-version-guarded patches serialized with normal lifecycle
  mutations, roll the workload, and refresh `cohesion-export`; immutable Jobs are replaced.
- One application-set cluster session — resolved member models share a single client and observer;
  `ImportFromKubernetes` reads each source gateway's exported model through operator kubeconfig
  trust before reconciliation.
- NuGet `buildTransitive` metadata — contributes the statically generated `kubernetes` gateway
  provider and declares `RequiresJit=true`.

**Design item 35 (`L04.01.02.07` / #32) integration:** an optional `ImageIndexPath` makes Gather
read the validated `cohesion/images/v1` application index and resolve only the resource's own
`ArtifactRef.Self` entry. The index application and repository/digest must match the manifest.
Relative archives are validated before use, and `ContainerRegistry` prefixes only a
`<late-bound>` entry on the non-Kind registry route. In Development, a selected
`kind-<cluster>` context loads an advertised
archive through `kind load image-archive --name <cluster>` once per context/digest; the process
inherits `KIND_EXPERIMENTAL_PROVIDER` and the pod keeps the archive's published repository rather
than receiving a registry prefix. A missing Kind executable warns and skips the daemon-load
attempt; late binding then uses a configured registry or fails, never an implicit pull. A no-index custom realizer may acquire only the
digest-pinned manifest identity; the direct-digest path and hermetic render surface remain
available. Export and Kubernetes import are included. Development port-forwarding, registry
reachability topology, bootstrap output, and render CLI integration require the later/upstream
surfaces described below.

**Dependencies:** the generic ApplicationModel + Gateway base packages, `platforms/Containers`,
and `KubernetesClient`. COHPLT001 forbids resource-area `.ApplicationModel`, `*.Hosting`,
`.Application` runtime, and `Microsoft.Extensions.*` assemblies from the resolved closure.

**Lifecycle:** `StopAsync` leaves persistent cluster state in place; `UninstallAsync`
(`--mode teardown`) removes managed objects and the namespace in best-effort reverse order.
The built-in controller explicitly registers each reached delete participant, and namespace deletion commits
only when every expected built-in participant succeeds; this distinction does not depend on run mode.
Partial startup rollback therefore does not wait for resources that were never reached, and a
committed namespace delete is polled to `NotFound` within `StopGrace`. The
base currently provides no equivalent uninstall/outcome signal for a custom-controller-only or
external-only model, so that model's namespace is conservatively preserved pending an upstream
lifecycle seam. Mixed models with a custom-controlled local resource also preserve the namespace
because that resource cannot report the outcome needed to authorize namespace deletion.

**AOT:** no mandate in this repo (owner decision, 2026-07-20). Provider metadata advertises
`RequiresJit=true`; see `docs/DESIGN.md`.

## Upstream compatibility boundary

- `ResourcePlan` has the requested replica count but no manifest `maxReplicas`; Cohesion validates
  the bound before reconciliation. It also lacks the manifest control-plane endpoint/path, private
  endpoint URI scheme, and restart policy, so only explicit plan probes can be compiled 1:1 and a
  Job cannot preserve `OnFailure` versus `Never` today.
- The current application runner rejects `Render` and `Bootstrap` before gateway dispatch, while
  generated gateway command-line handling has no platform-option hook for `--context` (the direct
  `UseKubernetesGateway(args)` overload supports it). Those
  surfaces remain upstream CLI work, not hidden parsing in this provider. The common
  application-model parser already carries `--adopt` through `IApplicationModel.Adopt`.
- Current resolver/model contracts carry neither a managed port-forward lifetime nor a gateway
  topology role. Development port-forwarding and Production root-owner enforcement need those
  upstream seams; this package still enforces namespace/object ownership and explicit adoption.
- The upstream application builder refuses `--realize` for Kubernetes before reconcile and directs
  Development use to Local, InProcess, or Docker.

**Status:** design item 33 delivered the contract/package gate; item 34 delivers the generic
Kubernetes plan compiler/controller and provider metadata. Item 35 adds application-index
resolution, late-bound registry identity, and the Development Kind archive-load path; arbitrary
non-Kind registry reachability remains tracked separately.
