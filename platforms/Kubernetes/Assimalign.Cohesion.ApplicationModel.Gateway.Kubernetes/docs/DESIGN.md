# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Design

> Implementation record for developer-experience design items 34, 35, and 37
> (`L04.01.03.09`, cohesion-platforms#31; `L04.01.02.07`, cohesion-platforms#32). The authority is
> `docs/DEVELOPER_EXPERIENCE_DESIGN.md` (signed 2026-09-06) and
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
  cluster contact. Each compilation reports unknown hint keys; reconciliation warns once per
  distinct key in each gateway session and otherwise ignores them.
- **Plan-shaped output.** Workload kind maps directly to Deployment, StatefulSet, DaemonSet, or
  Job. The compiler also emits Services, claims/PVC templates, exposures, probes,
  runtime-contract values, and `plan.Workload.StopGraceSeconds`. ConfigMaps and Secrets are
  compiled from the plan plus the resolved reconciliation inputs supplied by the gateway. Images
  remain digest-pinned.
- **Inputs beside the plan stay beside the plan.** Artifact identity, resolved mounts, bootstrap
  and trust material, and the immutable observed-dependency snapshot are compiler inputs but are
  not serialized back into `ResourcePlan`. The `cohesion.io/plan-hash` annotation hashes only the
  plan; credential rotation, observations, and per-resource patches are never plan drift.
  The controller maintains a separate opaque `cohesion.io/runtime-input-revision` on ConfigMap,
  Secret, and pod-template metadata. Changes to ConfigMap values or non-bootstrap Secret mounts
  advance it and roll pods; bootstrap-token changes are excluded because its projected Secret
  volume updates in place; `bootstrap-token` is reserved even when no credential is present. A
  compiled, workload-kind-aware `cohesion.io/workload-revision` on every pod template lets
  readiness ignore overlapping pods from an older rollout without changing the plan hash.
- **Gather resolves only the resource's own image.** When `ImageIndexPath` is configured, Gather
  reads the source-generated `cohesion/images/v1` application index, requires its application to
  match the manifest, validates the required lowercase OCI `platform`, and resolves the plan's
  `ArtifactRef.Self` by `ResourceName`. The authority-free repository and digest must exactly
  match the digest-pinned manifest artifact before registry resolution. A concrete entry registry
  authority is pinned and cannot be overridden. A configured `ContainerRegistry` prefixes the
  repository only when `registry` is omitted or null on the non-Kind route, and a tag is never a
  pull reference. Optional `archive` is relative to the index, must name an existing file, and is
  omitted when unavailable. A Local-environment Kind archive retains its published repository identity
  so containerd resolves the imported name and digest. With no index, an `IImageRealizer` may
  acquire the digest-pinned manifest artifact but must preserve its repository and digest; the
  direct digest-pinned artifact remains the other acquisition path.
- **Kind loading is a Local-environment gather action.** The same kubeconfig resolution used for the
  Kubernetes client supplies the current context. A `kind-<cluster>` context plus an advertised
  archive invokes `kind load image-archive <archive> --name <cluster>` before observer/client
  startup. Successful loads are deduplicated by context and digest for the gateway session. The
  process inherits the caller's environment, including `KIND_EXPERIMENTAL_PROVIDER=podman`, and
  honors cancellation. A missing executable warns and skips the command, after which a configured
  registry may supply late binding; unresolved late binding fails rather than falling through to
  an implicit pull. A started command's non-zero exit is an actionable gather failure.
  Non-Local and non-Kind paths do not start a process.
- **Observed state has one Kubernetes writer.** The single list+watch informer publishes locally
  realized lifecycle and `ResourceEndpoint` observations through the application-scoped state
  manager exposed by the gateway. A failing liveness signal can observe `Degraded`; it is never a
  readiness terminal and never re-gates an admitted dependent. The pod informer starts with a
  labeled list, watches from that list's resource version, and re-lists after watch failure or
  `410 Gone`. A bounded full resync also reads workloads, Services, and Service `Endpoints`, so a
  missed event cannot strand observed state. Restart history is tracked per pod UID and container,
  so pod replacement or a new workload revision cannot hide a later restart. When a public
  LoadBalancer address appears, changes, or disappears, the observer updates the ConfigMap's
  `COHESION_ENDPOINT_<EP>_PUBLIC_URL` and rolls the pod template before refreshing the export.
  This path is serialized with reconcile, stop, and teardown and uses narrow RFC 6902 patches with
  `resourceVersion` tests; it never reapplies a captured Secret, mount, or dependency snapshot.
  Because a Job template is immutable, a changed Job endpoint contract is foreground-deleted,
  awaited, and recreated from the current compilation. An interruption after deletion is repaired
  by normal reconciliation.
- **Plan-derived readiness.** Workload/pod status and plan probes are combined into the exact
  `ReadinessGate` carried by the plan. Long-running workloads gate on
  `{Running, Failed, Stopped}` and satisfy only on `Running`; their declared Services must also
  have ready `Endpoints`. Controller failure conditions are considered only after
  `status.observedGeneration` catches the applied generation, so a stale failure cannot abort a
  corrected rollout. Jobs satisfy on `Stopped`.
- **Service DNS is discovery.** Observed dependency snapshots project stable Service DNS to the
  canonical `System.Uri` runtime contract. A StatefulSet uses its governing Service. In-cluster
  dependencies never receive a NodePort or Ingress allocation.
- **Owned namespaces.** Each application namespace carries `cohesion.io/owner`; a foreign owner
  is refused unless explicitly adopted. The field manager is the gateway identity. Existing
  objects are ownership-preflighted before the first write, absent objects use create-before-apply
  conflict handling, forced applies carry a `resourceVersion`, and deletes carry
  UID/resource-version preconditions. Namespace-wide preflight rejects cross-plan Service and PVC
  identities. StatefulSet claim prefixes are checked against an unfiltered namespace PVC list,
  so an unlabeled retained or foreign claim cannot bypass ownership validation. Claim-template
  identity remains plan-owned after patches, and an existing claim's `cohesion.io/resource` must
  match even during application-level adoption. Namespace writers remain an operator RBAC trust
  boundary: pre/post claim lists detect conflicts, while Kubernetes may create ordinal claims
  asynchronously after the controller is applied.
- **Secret-free federation.** The gateway writes the base gateway's validated application export
  (internal/public endpoints, public trust key, and model document) to `cohesion-export`, updating
  it when the observer discovers or withdraws a public address.
  `ImportFromKubernetes` reads it through the selected kubeconfig context as an operator trust
  channel; application trust is neither inferred from nor required for ordinary namespace reads.
- **One application-set session.** A root `IApplicationSet` resolves member model documents from
  their area-gateway exports, then reconciles all member models through one Kubernetes client and
  the same observer. The provider never opens a second production cluster in that session.
- **Level-triggered replacement.** At session start, an application-wide resource-labeled object sweep
  removes workloads and ephemeral support objects whose resource label is absent from the current
  model, waits for each deletion to become observable, and preserves all PVCs. Per-resource reconciliation then preflights stale ownership,
  applies desired objects, and removes obsolete owned objects while preserving StatefulSet-generated PVCs. A
  changed immutable Job specification is foreground-deleted, awaited, and recreated. StatefulSet
  claim-template changes and other patched immutable controller fields are refused before any
  apply. Every retained generated claim must match the desired storage specification (a separately
  expanded request is accepted). Owner adoption recreates only the StatefulSet controller, after
  refusing a `whenDeleted=Delete` claim-retention policy or any PVC owner reference to the
  controller being removed; it preserves server-defaulted claim
  templates, rewrites their Cohesion owner metadata, and adopts existing generated PVC metadata
  separately. Removed-resource StatefulSets receive the same retention guard before deletion.

## Why-this-not-that decisions

- **One plan controller, not per-resource controllers.** Domain-authored overrides registered in
  `ApplicationGatewayOptions.Controllers` are consulted first, then the base external controller,
  then the built-in Kubernetes plan controller. The first `CanRealize(plan, out reason)` match
  wins. Rejected alternative: controllers keyed on kind or CLR capability interfaces — it creates
  a platform × resource-area matrix and makes new plan fields silently disappear.
- **Stop is not teardown.** `StopAsync` releases observation and runtime supervision while leaving
  Kubernetes objects and persistent state intact. `UninstallAsync` (`--mode teardown`) deletes
  resources and the namespace in best-effort reverse order. Stop and uninstall attempt every
  remaining resource, wait for each delete to become observable, and report the first failure
  afterward; startup rollback preserves its
  original failure while suppressing cleanup failures. The plan controller explicitly begins a
  teardown outcome and the observer commits namespace deletion only after every built-in
  controller participant reached in that session reports a successful delete, independently of
  the model's run-mode value. This makes partial startup rollback finite. A failed outcome leaves
  the namespace intact and closes the cancelled informer session so a later call can re-list and
  retry. A newly reconciled resource generation invalidates any previously completed teardown
  outcome for that namespace. Once committed, namespace deletion is polled to `NotFound` within `StopGrace`; a failed
  delete never pre-marks the namespace.
- **`KUBECONFIG` is a path list.** Resolution mirrors kubectl: explicit `KubeConfigPath` →
  `KUBECONFIG` (first existing entry in the platform path-separated list) → default
  `~/.kube/config` → in-cluster configuration. Failure is actionable.
- **Image acquisition stays out of the compiler.** Index parsing, late binding, and Kind loading
  happen in Gather. The compiler still receives one validated `IContainerImageArtifact` beside the
  immutable plan and always emits `repository@digest` with `IfNotPresent`; direct render never
  reads an index or contacts a process/cluster. Embedded-registry node reachability is deliberately
  left to the topology work in cohesion-platforms#22.

## AOT posture

No mandate: gateways are deploy-time control planes (owner decision, 2026-07-20).
The package's transitive provider metadata sets `RequiresJit=true`, so `Sdk.Gateway` auto mode
does not select NativeAOT for this KubernetesClient-based provider. Source-generated serialization
remains preferred for dependency and startup hygiene.

## Non-goals

- Helm and kustomize are not the desired-state source. The compiler's deterministic object graph
  is emitted by `--mode render` through the upstream renderer dispatch, without contacting a cluster.
- No single gateway reconciles multiple clusters. Federation is composed from one gateway per
  cluster, peer control planes/exported models, external references, and `IApplicationSet`.

## Delivery boundary and upstream gaps

Design item 34 replaces the pre-plan, capability-interface controller direction with the generic
compiler/controller path and packages the `kubernetes` `CohesionGatewayProvider`. Design item 35
adds application-index resolution, pinned or target-supplied registry identity, and the Kind
archive-load path while leaving the compiler unchanged. Both the acquisition component and Kind
command runner are tested through internal seams; the ordinary suite requires no live cluster,
and the real Kind smoke is explicitly opted in and dynamically skipped when its prerequisites are
absent.

The following criteria cannot be completed honestly inside this package against the current
upstream contract:

- `ResourcePlan` contains requested `replicas`, but not manifest `maxReplicas`. The bound remains
  an upstream build-time validation; the compiler realizes only the validated requested count.
- Control-plane endpoint/path, URI schemes, restart policy, and certificate mount names now
  arrive in the additive plan contract. These are compiler inputs of record; no manifest-kind
  dispatch or parallel field lookup is needed. Explicit probes remain 1:1; omitted readiness
  becomes HTTP GET at the control-plane port/path. Kubernetes Deployment/StatefulSet/DaemonSet
  require `Always`, warning when the declared restart policy is clamped; Jobs retain `OnFailure`
  or `Never`. Warnings flow through compilation warnings to the configured WarningHandler.
- The upstream base keeps telemetry header bytes private and currently exposes no subclass
  carrier. The compiler has an optional internal `ReadOnlyMemory<byte>` input, empty by default;
  normal reconcile passes none. The platform never invents telemetry endpoints/protocols.
- A live upstream factory creates one control-plane server per application through
  `Create(application)`, each exposing fixed `/cohesion/v1` routes. The current system Service
  maps one HTTP port, so several servers would collide at `0.0.0.0:8080`; a shared Ingress host
  cannot route them without an upstream root/member addressing contract. The observer rejects
  live multi-model control-plane sessions before workload/client-session mutation; the base may
  already have initialized trust before this hook. Bootstrap apply rejects several models before
  API access. Offline multi-model render and emit-only bootstrap support review, not a claim that
  the unresolved multi-model system installation is deployable. Existing multi-model realization
  remains unchanged when no control-plane factory is configured.
- The current model/resolver contracts expose neither a port-forward lifetime nor a root-versus-
  member gateway topology marker. Development port-forward management and enforcement that only
  the root `IApplicationSet` gateway owns Production therefore require upstream seams; namespace
  ownership and explicit adoption are enforced here regardless.
- `ApplicationGateway.GatherAsync` receives only `IApplicationResource`, not its owning model or
  plan. Kubernetes validation therefore records the Development flag and `ArtifactRef` against the
  exact resource instance before Gather. `ImageIndexPath` identifies one application index; a
  first-class application-to-index resolver is still needed for an application-set session that
  consumes distinct index files for different member applications.
- The base observer lifecycle hook does not identify whether it was stopped for `StopAsync`,
  `UninstallAsync`, or startup rollback, and a custom controller cannot report its delete outcome
  to this package. The built-in plan controller records the distinction explicitly, but an
  external-only or custom-controller-only model has no safe signal on which to delete its
  namespace; it is preserved rather than risking deletion after a failed custom teardown. A mixed
  model containing a custom-controlled local resource also preserves its namespace even when all
  built-in delete outcomes succeed.
- The upstream application builder rejects `--realize` when this provider is selected, before
  gathering or reconcile, and directs Development callers to Local, InProcess, or Docker. No
  Kubernetes-specific resource-area or external-realization branch is added to the compiler.

These gaps are explicit compatibility boundaries, not invitations for Kubernetes to inspect a
resource-area manifest or parse process arguments behind the gateway contract.

## System infrastructure and control-plane exposure (item 37)

`KubernetesSystemInstallation` is a separate internal builder of ordered Kubernetes objects. It
never creates or feeds a synthetic ResourcePlan into the one resource-plan compiler (rule 10).
The order is namespace, service account, namespaced and cluster RBAC, export/state PVC,
trust-key Secret, one-replica digest-pinned gateway
Deployment, ClusterIP control-plane Service, and optional LoadBalancer Service or Ingress.
Cross-namespace installations grant the existing reconciliation permissions at cluster scope.
Even a same-namespace installation needs a limited ClusterRole: namespace get/patch for the
system namespace and pod list/watch for the existing all-namespace informer.
Every metadata object is created through KubernetesMetadata. Infrastructure and discovery remove
the application resource label and use `cohesion.io/system=gateway`; this keeps the application
resource sweep from pruning them, including when an application shares the system namespace.
Application namespace teardown explicitly preserves SystemNamespace; bootstrap state remains
installed independently of application teardown. The normal owner/adopt rules still govern a
shared namespace. Its namespace annotation uses the application's owner; other infrastructure
uses the configured field manager as owner.

SystemNamespace and SystemServiceAccount default to `cohesion-system` and `cohesion-gateway` and
are DNS labels. SystemImage has no default and is validated by ContainerImageArtifacts.Create;
SystemStorageSize must be explicitly supplied for the persistent export/trust state installation.
SystemStorageClass is optional. SystemExposure defaults to None; Ingress requires an explicit DNS
host and controller class. BootstrapApply defaults to true. The deployment uses a Recreate strategy
with one replica and a writable state PVC, plus a read-only Secret projection. The gateway's
protected bind hook supplies an IP HTTP endpoint reachable through port 8080. The current upstream
hosting-free server only supports HTTP/IP binding; platform exposure does not claim new server TLS
support. Ingress/controller/org TLS policy remains deployment configuration.

`KubernetesGatewayTrustKeyRepository` implements the upstream public native persistence seam.
It loads or creates P-256 PKCS#8 keys in the owned Opaque system Secret, under distinct
`<application>.<gateway>.p8` entries, and rotates only the selected identity. Atomic create races
load the winner; resource-version-conditional, non-forced updates preserve other identities.
Foreign owners, corrupt/trailing data, and non-P256 keys fail closed, including on rotation.
Temporary key buffers are cleared. A caller's custom repository is preserved. Render/bootstrap
never mint a key, token, or certificate. The base still issues ES256 developer tokens with audience
`cohesion-export`, and the real upstream control-plane implementation verifies them. Only public
trust keys appear in discovery ConfigMaps. PVC state remains writable for upstream exports and
other gateway metadata; the projected Secret never occupies that writable directory.

`KubernetesGatewayCommandLine.Apply` is a public static SDK seam, a narrow documented deviation
from the interface-first convention. The old direct-extension parser body/helpers were moved here
and extended; the extension delegates this same parser. The exact fully qualified method appears
in buildTransitive metadata. The SDK invokes it before GatewayControlPlane.Configure captures the
export directory; the emitted pod environment selects `/var/lib/cohesion`, while developer hosts retain upstream `.cohesion` defaults. Recognized values support both
separated and equals forms, optional bootstrap booleans support bare and explicit forms, and
unknown switches remain available to other parsers. Missing values identify their switch.

The existing application export ConfigMap additionally carries `control-plane.json` using the
upstream `{url,trustKey}` metadata shape. None uses stable Service DNS; LoadBalancer uses its first
allocated hostname/IP; Ingress uses its configured host. No allocated LB address means no metadata
entry. The same observer resync polls system allocation independently from workload changes and
publishes only address changes, including withdrawal. A missing system Service is pending rather
than a fabricated public URL. IKubernetesApplicationModelResolver retains the upstream model
resolver interface and exposes separate operator-channel URL resolution for `remote.Gateway(url)`.
It neither changes peer authentication nor stores bearer credentials.

## Offline rendering and bootstrap application

The renderer gathers every output document before writing, emits system infrastructure first,
then each model/resource in declaration order using the existing deterministic object renderer.
It resolves digest identity from manifests or a local index and never calls Gather, Kind, image
realizers, registries, or Kubernetes. Inputs are ResourceInputs.Empty. A render-only compiler preview
flag preserves declared mount shapes while omitting unresolved bytes; strict per-resource Render
and live compilation still reject unresolved inputs. These previews require runtime resolution
before deployment. A PEM certificate bundle stays unsplit at its existing Secret mount in an
Opaque Secret, without a new Secret type or extra volume. Token, trust bundle, and optional telemetry
headers share the existing private bootstrap projection; an empty input adds no payload or path,
and stale credential-path environment values are removed. Telemetry endpoint/protocol values in
the immutable plan are left alone.

The certificate carrier deliberately remains the existing Opaque Secret volume, rather than
`kubernetes.io/tls`: the contract is one unsplit PEM document containing the leaf, private key, and
chain, whereas the TLS Secret type requires separate `tls.crt` and `tls.key` entries. No new
certificate Secret, volume, or type is needed. `PortBinding.Certificate` is validated against a
Secret mount before compilation; the case-insensitive reserved value `public` needs no mount.

`inputs.TrustBundle` is reserved key `trust-bundle`, projected as `trust.pem` and advertised by
`COHESION_TRUST_BUNDLE_PATH=/var/run/cohesion/trust.pem`. The empty-by-default internal
`ReadOnlyMemory<byte> telemetryHeaders` compiler parameter is reserved key `telemetry-headers`,
projected as `telemetry.headers` and advertised by
`COHESION_TELEMETRY_HEADERS_PATH=/var/run/cohesion/telemetry.headers`. Both join `bootstrap-token`
in the same `cohesion-bootstrap` projected volume with mode 0400 and a read-only mount. Trust-only
and telemetry-only inputs each produce exactly one projection item; all three empty means no
projection volume. Reserved mount names are rejected even when their injected input is absent.
No compiler assigns `COHESION_TELEMETRY_ENDPOINT` or `COHESION_TELEMETRY_PROTOCOL`. The optional
telemetry carrier changes no render output when empty.

Bootstrap always writes system output. BootstrapApply=false then returns without platform contact.
True performs ownership preflight across the complete ordered object set before the first mutation,
uses create-before-apply conflict detection for absent objects, and uses resource versions for
checked server-side applies with the configured field manager. A foreign owner requires model
adoption; an existing trust Secret's data is preserved. This intentionally follows the approved
emit-or-apply contract even though IApplicationGatewayBootstrapper XML currently describes offline
emission only. Bootstrap does not feed system objects to the resource-plan compiler.

The hermetic suite covers bootstrap ownership and races, native trust persistence, real upstream
loopback token verification, and rendered goldens for all three system exposures and the composed
resource stream. The five resource goldens consume the post-31t upstream fixtures. For explicit
golden regeneration, set `COHESION_RENDER_OUTPUT` to a scratch directory and run the render tests:
they export actual output under `Kubernetes/` while retaining their checked-in golden assertions.
Review the output before copying it into `tests/Fixtures`, then run the render tests twice.
COHPLT001 follows the owner-corrected rule 3: the Gateway base may transitively bring Hosting,
Hosting.Health, and Hosting.Resources; direct platform references to that trio remain forbidden.
The emitted gateway pod carries private `COHESION_KUBERNETES_FIELD_MANAGER`,
`COHESION_KUBERNETES_STATE_DIRECTORY`, and optional `COHESION_KUBERNETES_INGRESS_CLASS` environment
values. The public hook reads these before arguments, preserving installation ownership, writable
mount location, and controller class without adding resource-runtime keys or new CLI switches.
Exposure/host/service-account settings travel through existing switches. The gateway image must
still execute its SDK-generated option hook; custom Program option behavior is the operator's
responsibility. Same-namespace installations align the Namespace annotation with model.Owner;
other infrastructure remains owned by FieldManager. Offline composed render emits each additional
application Namespace before its resource objects.
