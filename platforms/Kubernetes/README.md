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
| `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` | `KubernetesGateway : ApplicationGateway` (`Name = "kubernetes"`), image-index/Kind acquisition, `KubernetesPlanCompiler`, plan controller, informer/observer, and `UseKubernetesGateway()`. Design item 34 is tracked by [`L04.01.03.09` / #31](https://github.com/assimalign/cohesion-platforms/issues/31); shared image acquisition is [`L04.01.02.07` / #32](https://github.com/assimalign/cohesion-platforms/issues/32). |

## Design item 34 delivery

- `KubernetesPlanCompiler` accepts every validated `cohesion/plan/v1` plan through one generic
  path. It maps `plan.Workload.Kind` to Deployment, StatefulSet, DaemonSet, or Job and compiles
  Services, headless governing Services, volume claims/templates, probes, runtime-contract
  environment, and deterministic Cohesion metadata. Artifact identity, resolved mounts,
  credentials, and observed dependencies are reconciliation inputs beside the immutable plan.
- `KubernetesPlanController` performs level-triggered server-side apply, prunes obsolete owned
  objects both within a plan and for resources removed from the application model, replaces changed
  immutable Jobs, and performs idempotent reverse deletion. Removed-resource PVCs are retained. Ownership is
  preflighted before writes; apply/delete races are bounded with resource-version and UID
  preconditions, while absent objects use create-conflict rechecks. Service names, standalone PVC
  names, and StatefulSet claim prefixes are checked across the entire namespace; generated claims
  are found through an unfiltered PVC list before adoption, and their resource identity cannot be
  reassigned by an application-level `--adopt`. Retained claims must match the template storage
  contract, with separately expanded capacity accepted, and controller deletion is refused when a
  StatefulSet policy or a lingering controller owner reference would delete its claims.
  Removed-resource deletions are awaited before reconciliation continues. Namespace write
  permission remains an operator RBAC
  trust boundary because ordinal claim creation is asynchronous. An opaque
  runtime-input revision rolls pods when ConfigMap or mounted Secret content changes without
  changing plan drift; projected bootstrap-token rotation remains live. Stateful claim-template
  mutations and other patched immutable controller fields fail actionably before any apply.
- It does not wait for readiness; an initial labeled pod list followed from its resource version,
  plus a bounded full resync, publishes workload state, ready Service endpoints, and
  internal/public addresses for the plan-derived gate. A compiled workload revision filters old
  rollout pods from readiness and exit-code observations. Restart counters are tracked by pod UID
  and container so replacements cannot mask degradation. Observed LoadBalancer changes update the
  endpoint `PUBLIC_URL` contract value with a resource-version-guarded patch serialized against
  reconcile and teardown, roll the workload, and refresh the export without replaying captured
  Secret or configuration inputs. Immutable Jobs are replaced. Watch failures re-list before
  resuming.
- `cohesion.io/plan-hash` hashes the plan only. Rotating bootstrap credentials, observed
  dependencies, and per-resource patches do not masquerade as plan drift.
- The gateway writes the base application's secret-free export (internal/public endpoints,
  public trust key, and model document) to `cohesion-export` and refreshes it when observed public
  addresses change; `ImportFromKubernetes` reads it with the kubeconfig's operator identity,
  independently of application trust.
- A root `IApplicationSet` reconciles every resolved member model through the same Kubernetes
  client and observer when no control-plane factory is configured (see the addressing boundary below). Kubernetes-imported members are reconstructed from each area gateway's
  exported model document before that single-cluster reconcile begins.
- The NuGet package contributes the `kubernetes` `CohesionGatewayProvider` through
  `buildTransitive`, so `Sdk.Gateway` can generate its static provider switch without naming a
  platform type upstream.

## Design item 35 image acquisition

- `KubernetesGatewayOptions.ImageIndexPath` selects a source-generated, validated
  `cohesion/images/v1` `application.images.json`. Gather resolves only the current plan's
  `ArtifactRef.Self` entry, verifies that the index application matches the resource manifest,
  validates the required lowercase OCI `platform`, and requires the entry's authority-free
  repository and digest to equal the manifest's digest-pinned image. An advertised `archive` is
  resolved relative to the index without allowing escape and must exist; the field is omitted
  when unavailable. Gather locates and loads prebuilt image bits; it never builds an image.
- `KubernetesGatewayOptions.ContainerRegistry` is an authority without a URI scheme or repository
  path. It prefixes the authority-free repository only when entry `registry` is omitted or null.
  A concrete entry registry is pinned, takes precedence, and cannot be replaced by the option;
  tags remain metadata only. Acquisition routes are exclusive: a Development Kind archive keeps
  the entry's published repository identity so containerd can find the imported digest, while a
  non-Kind late-bound route applies the configured registry authority. Registry network
  reachability remains the topology work tracked by
  [#22](https://github.com/assimalign/cohesion-platforms/issues/22).
- For a Development model whose selected kubeconfig context is `kind-<cluster>`, an advertised
  archive is loaded with `kind load image-archive <archive> --name <cluster>` before Kubernetes
  client startup. Successful loads are deduplicated by context and digest for the gateway
  session. The child process inherits `KIND_EXPERIMENTAL_PROVIDER` (including `podman`); a missing
  `kind` executable produces a warning and skips the command. A configured registry then supplies
  late binding; without either successful loading or a registry, gather fails rather than allowing
  an implicit pull. Non-Kind and non-Development paths never invoke it.
- When `ImageIndexPath` is omitted, a custom `IImageRealizer` may acquire the manifest's declared
  image but cannot resolve a tag-only key or substitute another repository/digest. The direct
  digest-pinned-manifest path remains available. Render continues to accept an already resolved
  artifact and never reads an index, starts a registry, or invokes Kind.

## Design item 37 system installation and discovery

`KubernetesGateway` implements the upstream `IApplicationGatewayRenderer` and
`IApplicationGatewayBootstrapper` contracts. `--mode render` emits system objects first, followed
by every model and resource plan in declaration order. It reads a configured image index offline,
validates digest identity, and never gathers images, invokes Kind, or contacts a cluster. Unresolved
mounts retain empty ConfigMap/Secret shapes; resolve inputs before applying resource previews.

`--mode bootstrap` always emits the installation and applies it by default. Use
`--bootstrap-apply=false` for offline emission. This intentionally follows the owner-approved
emit-or-apply mode semantics despite the upstream bootstrap interface XML's offline-only wording.
The system builder is separate from the resource-plan compiler (platform rule 10).

Configure `SystemImage` with a digest-pinned gateway executable image and `SystemStorageSize`
with an explicit PVC capacity; neither has a fabricated default. `SystemNamespace` defaults to
`cohesion-system`, and `SystemServiceAccount` to `cohesion-gateway`. Optional `SystemStorageClass`
selects the storage class. Bootstrap emits Namespace, ServiceAccount, Role/RoleBinding,
ClusterRole/ClusterRoleBinding, writable export/state PVC,
trust-key Secret, one-replica Deployment, and the `cohesion-control-plane` ClusterIP Service.
Same-namespace cluster permissions cover namespace get/patch and the existing global pod
list/watch; reconciling other namespaces requires broader cluster permissions.
Infrastructure uses `cohesion.io/system=gateway`, never application resource labels, so resource
pruning preserves it. Application teardown also preserves the system namespace.

`SystemExposure` is `None` (default), `LoadBalancer`, or `Ingress`. LoadBalancer adds a public
Service; Ingress requires both `SystemIngressHost` and `SystemIngressClass`. The
upstream server binds `http://0.0.0.0:8080`; its HTTP transport remains an upstream limitation.
The secret-free `cohesion-export` ConfigMap contains existing `export.json` plus
`control-plane.json` with the upstream `{url, trustKey}` shape. None advertises Service DNS,
LoadBalancer its allocated hostname/IP, and Ingress its configured host. Missing or pending
LoadBalancer allocation withholds metadata. The existing observer resync refreshes allocation,
change, and withdrawal independently of workload endpoint changes.

`ImportFromKubernetes` returns `IKubernetesApplicationModelResolver`; its
`ResolveControlPlaneAddressAsync` exposes the URL for `remote.Gateway(url)`, while model import
continues implementing upstream `IApplicationModelResolver`. Kubeconfig is the operator channel;
no developer or bootstrap tokens are placed in ConfigMaps. The native trust-key repository uses
the public upstream `IGatewayTrustKeyRepository` seam, preserves per-application/gateway P-256
keys in the owned Secret, and leaves developer token issuance and verification upstream.

The package's public `KubernetesGatewayCommandLine.Apply(KubernetesGatewayOptions, string[])`
is the exact buildTransitive SDK hook. It runs before `GatewayControlPlane.Configure`, establishing
the deployment-provided export/metadata mount path. The direct extension delegates the same parser.
Valued switches accept separated and `=value` forms: `--context`, `--kubeconfig`,
`--cohesion-system-namespace`, `--cohesion-system-image`, `--cohesion-system-service-account`,
`--cohesion-system-storage`, `--control-plane-expose`, and `--control-plane-host`.
`--bootstrap-apply` accepts a bare flag, `=true|false`, or a following boolean. Unknown switches
are ignored; missing and empty recognized values name the offending switch.

## Layering & posture

- Depends on the generic ApplicationModel contract + Gateway base (NuGet),
  `platforms/Containers`, and `KubernetesClient` (centrally pinned). It never references a
  resource area's `.ApplicationModel`, `*.Hosting`, `.Application` runtime, or
  `Microsoft.Extensions.*` assembly.
- **AOT:** this repo carries no `IsAotCompatible` mandate (owner decision, 2026-07-20). The package
  advertises `RequiresJit=true`, preventing `Sdk.Gateway` auto mode from selecting NativeAOT for a
  KubernetesClient-based gateway. A typed REST client on Cohesion's own HTTP stack remains a
  dependency-hygiene option, not an AOT necessity.
- Development target: **Kind on Podman** (`KIND_EXPERIMENTAL_PROVIDER=podman`), daemon-load image
  path first; registry topologies later ([#22](https://github.com/assimalign/cohesion-platforms/issues/22)).
- Plan probes combine with workload/pod status to produce the plan-derived readiness gate.
  Ready Service `Endpoints` are also required for long-running workloads. Probe/restart-driven
  `Degraded` is observed after readiness and never re-gates dependents. Observed dependency
  addresses use stable Kubernetes Service DNS. Stale controller failure conditions are ignored
  until `status.observedGeneration` catches the desired generation.

## Lifecycle and non-goals

- `StopAsync` releases observation and runtime supervision in best-effort reverse order while
  leaving persistent cluster state in place. `UninstallAsync` (`--mode teardown`) removes managed
  objects and the application namespace in best-effort reverse order; deletion is idempotent and
  later deletions are still attempted after a failure. The built-in controller explicitly records
  each reached uninstall participant and commits namespace deletion only after every expected
  built-in participant succeeds, independently of model run mode. Partial startup rollback is
  scoped to the resources it reached; committed namespace deletion is awaited within `StopGrace`.
  The upstream observer hook exposes no uninstall or
  delete-outcome signal for custom-controller-only/external-only models, so their namespaces are
  conservatively preserved pending that seam; a mixed custom-controller model also preserves its
  namespace rather than risking deletion without its local outcome.
- One gateway never reconciles multiple clusters. Federation uses one gateway per cluster plus
  peer control planes, exported models, external references, and `IApplicationSet`; cross-cluster
  references are supported without turning one gateway into a multi-cluster reconciler.

## Upstream contract boundaries

Design item 34 deliberately does not invent data or command hooks that the current Cohesion
contracts do not expose:

- `ResourcePlan` carries control-plane endpoint/path, URI schemes, restart policy, and certificate
  mount names. Missing explicit readiness maps to its declared control-plane HTTP GET. Jobs retain
  `OnFailure`/`Never`; other workloads require `Always`, with a warning when clamped. Manifest
  `maxReplicas` remains upstream validation rather than a platform-specific setting.
- Certificate PEM bundles remain a single unsplit `Opaque` Secret value at the declared Secret
  mount. No new TLS Secret type or volume is introduced. Bootstrap token, trust bundle, and optional
  telemetry headers share the existing private projection; empty inputs add no payload or path.
  The base currently exposes no telemetry-header carrier to platform subclasses, so the internal
  optional compiler input remains empty during normal reconcile. No endpoint or protocol is invented.
- The upstream factory creates a separate fixed-route HTTP server per application. Several models
  cannot share the one system listen port/Ingress host without an addressing contract. A live
  multi-model session with a control-plane factory fails before workload session mutation (upstream
  trust initialization can precede the observer hook); multi-model bootstrap apply is refused
  before API access. Offline rendering/emit-only bootstrap remains available for review and does
  not imply that the unresolved multi-model installation can be deployed.
- Existing resolver/model contracts provide neither a managed Development port-forward lifetime
  nor a root-versus-member gateway role. Those features need upstream seams before the package can
  manage forwarding or reject a standalone per-area Production gateway reliably.
- Cohesion's application builder already refuses `--realize` for the Kubernetes gateway and names
  Local, InProcess, and Docker as the Development-only alternatives; the plan controller therefore
  never receives a locally realized cross-application plan.
- The optional image-index path names one application's index. The upstream gateway gather hook
  supplies a resource but not its owning model, so validation records the model's Development
  flag and `ArtifactRef` for that exact resource before gathering. A future application-to-index
  resolver is required if one gateway session must consume distinct index files for multiple
  member applications.

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing.

## Opt-in Kind verification

Normal tests need no cluster. The explicit smoke requires Podman installed with its engine or
machine running, Kind and kubectl on PATH, an existing Kind cluster created with
`KIND_EXPERIMENTAL_PROVIDER=podman`, a matching kubeconfig context, and a prebuilt OCI archive
whose image is named by its SHA-256 manifest digest. The archive must match the node architecture.
Set `COHESION_RUN_KIND_SMOKE=1`, `COHESION_KIND_SMOKE_CLUSTER=<cluster>`,
`COHESION_KIND_SMOKE_ARCHIVE=<absolute archive path>`, and
`COHESION_KIND_SMOKE_DIGEST=sha256:<64 hex characters>`; retain
`KIND_EXPERIMENTAL_PROVIDER=podman` for cluster enumeration and loading. Run the Kubernetes test
project with filter `FullyQualifiedName~KindImageLoaderSmokeTests` and inspect TRX results: exactly
one executed passing test is required. Missing prerequisites cause a skip, which is not smoke
validation. This smoke exercises archive loading only; it does not prove system installation,
Ingress/LB reachability, or control-plane authentication on a real cluster.
