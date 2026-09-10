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
  client and observer. Kubernetes-imported members are reconstructed from each area gateway's
  exported model document before that single-cluster reconcile begins.
- The NuGet package contributes the `kubernetes` `CohesionGatewayProvider` through
  `buildTransitive`, so `Sdk.Gateway` can generate its static provider switch without naming a
  platform type upstream.

## Design item 35 image acquisition

- `KubernetesGatewayOptions.ImageIndexPath` selects a source-generated, validated
  `cohesion/images/v1` `application.images.json`. Gather resolves only the current plan's
  `ArtifactRef.Self` entry, verifies that the index application matches the resource manifest,
  and requires the entry's repository and digest to equal the manifest's digest-pinned image.
  An advertised `archivePath` is resolved relative to the index without allowing escape and must
  exist. Gather locates and loads prebuilt image bits; it never builds an image.
- `KubernetesGatewayOptions.ContainerRegistry` is an authority without a URI scheme or repository
  path. It prefixes only entries whose registry marker is `<late-bound>`; fixed repositories are
  unchanged and tags remain metadata only. Acquisition routes are exclusive: a Development Kind
  archive keeps its published repository so containerd can find the imported digest, while a
  non-Kind route applies the configured registry authority. Registry network reachability remains
  the topology work tracked by [#22](https://github.com/assimalign/cohesion-platforms/issues/22).
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

- `ResourcePlan` carries the requested replica count but not manifest `maxReplicas`; the upstream
  validator enforces that bound, while the Kubernetes compiler realizes only the validated
  requested count. The plan also omits the manifest control-plane endpoint/path, private endpoint
  URI scheme, and restart policy, so an implicit control-plane readiness probe and Job restart
  choice cannot be reconstructed when absent from explicit plan data.
- Cohesion's application command runner currently rejects `--mode render` and `--mode bootstrap`
  before a platform gateway is invoked. Generated `UseGateway(args)` code applies common options
  only and has no platform-option hook for `--context`; direct `UseKubernetesGateway(args)` does
  parse `--context` and `--kubeconfig`. Render/bootstrap dispatch and generated platform options
  require upstream CLI work. `--adopt` is already a common
  application option and reaches the gateway as `IApplicationModel.Adopt`.
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
