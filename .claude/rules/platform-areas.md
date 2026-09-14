# Platform Areas (`platforms/**`)

Every `platforms/<Platform>/` folder is a **platform area**: the implementation of Cohesion's
application-model gateway contract for one deployment target. The authoritative upstream direction
is `docs/DEVELOPER_EXPERIENCE_DESIGN.md` (signed off 2026-09-06) and its realization-plan contract
`docs/REALIZATION_PLAN.md` in the cohesion repository. Older ApplicationModel design text is
historical wherever it disagrees.

<!-- Deviates from the prior repo platform rules 1, 2, 3, 5, 6, 7, and 9 per the owner-approved
Developer-Experience Design item 33 (signed 2026-09-06), including §12 deviations (5), (10), and
(11), R5, and O3/O13/O28–O30. The changes below are scoped to platform gateways. -->

## The gateway family shape

A platform gateway package is `Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>` living at
`platforms/<Platform>/Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>/{src,tests,docs}`. It
derives from the public guided base `ApplicationGateway` (package
`Assimalign.Cohesion.ApplicationModel.Gateway`) and supplies only platform hooks:

- `Name` — stable gateway identity (`"kubernetes"`, `"docker"`).
- Plan compiler/controller — exactly one built-in compiler per platform consumes `ResourcePlan`;
  domain-authored overrides registered through `ApplicationGatewayOptions.Controllers` are
  consulted first, in registration order. The framework supplies no overrides by default and
  never assembly-scans for them.
- `State` — cohesion's public `InMemoryResourceStateManager`, or another
  `IApplicationResourceStateManager` implementation with the same level-triggered contract.
- `GatherAsync` — **locates and validates** a deployable artifact; it never builds. Container
  gateways resolve a pre-built, digest-pinned image through the image index.
- `StartObserverAsync` / `StopObserverAsync` — the platform's single observer lifecycle.

## Hard rules

1. **Single-writer observed state.** For platform-realized resources, the platform's one observer
   is the only writer of observed lifecycle and endpoints into `State`; an external resolver owns
   only its external-resource observations. Controllers apply desired state and return. A failed
   liveness probe on a running resource is observed as `Degraded`; it never re-gates dependents.
2. **Controllers reconcile the plan.** A controller is an idempotent, level-triggered reconciler
   over the validated `ResourcePlan`; the compiler that produces platform objects is pure. A
   controller never blocks on readiness. The base gateway reads `plan.Workload.Gate`
   (`ReadinessGate`): long-running workloads complete on `{Running, Failed, Stopped}` and satisfy
   only on `Running`; Jobs complete on `{Stopped, Failed}` and satisfy only on `Stopped`.
   `Degraded` is non-gating.
3. **Layering.** A platform gateway may reference the `Assimalign.Cohesion.ApplicationModel`
   contract package, the `Assimalign.Cohesion.ApplicationModel.Gateway` base package, shared
   `platforms/Containers` libraries, and thin `<Area>.Client` packages only. It must never
   reference an `<Area>.ApplicationModel`, any `<Area>.Hosting` runtime module, any `<Area>.Application`
   runtime, or any `Microsoft.Extensions.*` assembly. The Core-only `Assimalign.Cohesion.Hosting` and
   `Hosting.Health` and the opt-in `Hosting.Resources` are not area runtime modules: the Gateway base package
   depends on them (shared command/mount types since cohesion item 23b), so they may appear in a platform
   gateway's closure; a platform gateway still never references them directly.
4. **Digest-pinned images.** Deployments always reference images as
   `{repository}@sha256:{digest}`. Tags are human-readable metadata only — never a pull reference.
5. **Observed endpoints are the discovery surface.** Observers publish `ResourceEndpoint`
   observations through the state manager; their address contract is `System.Uri`, constructed
   and validated with Core's `UriExtensions` before injection into a resource. Controllers compile
   from the dependency snapshots supplied on `ResourceControlContext`, never an independent state
   lookup. Kubernetes publishes stable Service DNS (the governing Service for a StatefulSet),
   never a NodePort or Ingress allocation for in-cluster dependency injection.
6. **One frozen runtime contract.** Platform injection uses the `COHESION_*` constants and helpers
   on `Assimalign.Cohesion.Core.ResourceEnvironment`; `docs/RUNTIME_CONTRACT.md` in the cohesion
   repository is the wire contract for non-.NET workloads, including the reserved bootstrap and
   telemetry keys. The gateway is the authoritative writer of frozen identity and endpoint-binding
   keys; manifest environment values are preserved except where the planning context must overwrite
   those identities, and platform-allocated values are supplied only where the plan leaves them
   unresolved.
7. **Plan probes plus platform-native observation.** Readiness, liveness, and startup come from
   the plan's probe mappings together with the platform-native signal: Kubernetes workload/pod
   status or Docker container health. Stdout markers are a LocalGateway assist, not a platform
   readiness mechanism.
8. **Failure semantics come from the base.** A resource that does not satisfy its plan-derived
   readiness gate blocks transitive dependents and aborts startup with best-effort reverse
   rollback through `DeleteAsync`. Do not re-implement or bypass this behavior in platform code.
9. **Stop is not teardown.** `StopAsync` releases runtime supervision and observation in
   best-effort reverse order while leaving persistent platform state in place. `UninstallAsync`
   (the `--mode teardown` path) removes platform resources in best-effort reverse order; deletion
   is idempotent and a failure does not prevent later resources from being attempted.
10. **One compiler per platform.** A platform compiler consumes the plan schema and is never keyed
    on manifest kind, resource area, or CLR type. An unknown specification field is an error; an
    unknown hint warns once and is ignored. A plan whose schema is newer than the compiler
    supports is refused during `Build()` or `--mode render`, before gathering or platform contact.

## Enforcement

`build/Targets/Build.Rules.targets` enforces rule 3 as **COHPLT001** for every shipped project
under `platforms/**`. Tests, samples, and examples are excluded by path. The target inspects both
the project-reference graph and the resolved compile/runtime assembly closure after
`ResolveAssemblyReferences`, so direct, transitive, hint-path, and package-delivered forbidden
assemblies all fail with every offending assembly named in the diagnostic.

## Area layout

- `platforms/Containers/` — shared container artifact, image-index, OCI-store, registry, and test
  infrastructure. It never owns a platform compiler or depends on a specific platform gateway.
- `platforms/Kubernetes/`, `platforms/Docker/` — one area and one plan compiler per target.
- Cross-area references flow **only** toward `Containers`; Kubernetes and Docker never reference
  each other.

## AOT posture

**No hard AOT mandate in this repo** (owner decision, 2026-07-20): gateways are deploy-time
control planes, so `KubernetesClient` and similar dependencies need no exception machinery. The
cohesion repo's deployed runtime libraries retain their AOT mandate. Keep source-generated
serialization as the default. Reintroducing a repo-wide mandate requires explicit owner
confirmation under `deviations.md`.

## Relaxing these rules

Changing a hard rule means updating this file, every affected area `README.md`, and any build
enforcement in the same commit under `deviations.md`. The changes above are already authorized by
the signed Developer-Experience Design; a different relaxation still requires explicit owner
confirmation.
