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
`ArtifactRef.Self` entry. The index application and authority-free repository/digest must match
the manifest, and the required lowercase OCI `platform` is validated. A concrete entry registry
is pinned and cannot be overridden; `ContainerRegistry` prefixes the repository only when
`registry` is omitted or null on the non-Kind route. Optional `archive` is relative, is validated
before use, and is omitted when unavailable. In Development, a selected
`kind-<cluster>` context loads an advertised
archive through `kind load image-archive --name <cluster>` once per context/digest; the process
inherits `KIND_EXPERIMENTAL_PROVIDER` and the pod keeps the archive's published repository
identity, including any pinned entry registry, rather than receiving a target registry override.
A missing Kind executable warns and skips the daemon-load
attempt; late binding then uses a configured registry or fails, never an implicit pull. A no-index custom realizer may acquire only the
digest-pinned manifest identity; the direct-digest path and hermetic render surface remain
available. Export and Kubernetes import are included; system installation, SDK render/bootstrap dispatch,
and the public platform CLI hook are implemented in item 37. Operational boundaries appear below.

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

- The additive plan facts now carry control-plane endpoint/path, endpoint schemes, restart policy,
  and certificate mount names. Default readiness uses that control plane; non-Job restart clamps
  to `Always` with a warning. Certificate PEM stays in its declared Opaque Secret mount.
- Telemetry headers have an optional internal compiler input sharing the bootstrap/trust volume.
  The upstream base has no public subclass carrier for the bytes, so it remains empty in normal
  reconcile; the platform never invents telemetry endpoint/protocol or credential values.
- Multiple model control-plane servers share upstream fixed routes and cannot currently share
  one system listen port. Live multi-model control-plane sessions and multi-model bootstrap apply
  fail explicitly pending a routing contract; offline multi-model rendering remains a review surface.
- Current resolver/model contracts carry neither a managed port-forward lifetime nor a gateway
  topology role. Development port-forwarding and Production root-owner enforcement need those
  upstream seams; this package still enforces namespace/object ownership and explicit adoption.
- The upstream application builder refuses `--realize` for Kubernetes before reconcile and directs
  Development use to Local, InProcess, or Docker.

**Status:** design item 33 delivered the contract/package gate; item 34 delivers the generic
Kubernetes plan compiler/controller and provider metadata. Item 35 adds application-index
resolution, pinned or target-supplied registry identity, and the Development Kind archive-load path;
arbitrary non-Kind registry reachability remains tracked separately.

## System and command modes

The separate system installation builder emits Namespace, ServiceAccount, namespaced and
cluster RBAC, a state/export PVC, owned trust-key Secret, digest-pinned one-replica
Deployment, and `cohesion-control-plane` Service. All metadata originates at KubernetesMetadata;
system objects use a separate system label so resource pruning and teardown preserve them.
Cluster permissions are limited to namespace get/patch and global pod list/watch when the
application shares the system namespace, and broaden for cross-namespace reconciliation.
`SystemNamespace=cohesion-system`, `SystemServiceAccount=cohesion-gateway`, `SystemExposure=None`,
and `BootstrapApply=true` are defaults. `SystemImage` and persistent `SystemStorageSize` are
explicit requirements for rendering/bootstrap; optional storage class and Ingress host/class are
platform options. Ingress requires both host and class; LoadBalancer adds a separate Service.

`RenderAsync` emits installation first then models/resource plans in declaration order with empty
resolved inputs, offline manifest/index artifacts, and no Gather call. Unresolved mount objects
are previews requiring runtime input resolution. `BootstrapAsync` always emits and optionally
applies; false is offline emission. Default application intentionally follows the owner-approved
mode contract despite upstream bootstrap XML describing an offline-only operation.

The SDK calls public `KubernetesGatewayCommandLine.Apply` before `GatewayControlPlane.Configure`;
the hook reads the deployment-provided mounted export path and parses the same switches as the direct extension.
Discovery metadata uses upstream `{url,trustKey}` alongside existing `export.json`, with Service
DNS, allocated LoadBalancer host/IP, or Ingress host. The observer's periodic resync refreshes system
allocation independently. The typed Kubernetes resolver exposes this URL for `remote.Gateway(url)`.
Only public trust material belongs in ConfigMaps. Native P-256 key persistence uses the public
`IGatewayTrustKeyRepository` seam; upstream still issues and verifies developer export tokens.

See [the area README](../../README.md) for switches and exact opt-in Kind prerequisites.
