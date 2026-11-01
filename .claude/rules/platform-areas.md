# Platform Areas (`platforms/**`)

Every `platforms/<Platform>/` folder is a **platform area**: one deployment target of Cohesion's
application model, shipped as a **platform pair** (cohesion's application-model restart,
assimalign/cohesion#1759, contracts decision C5 "platform pairs"). The upstream contracts are
cohesion's `Assimalign.Cohesion.ApplicationModel.Gateway` (the gateway's provider and instance-client
seams) and `Assimalign.Cohesion.ApplicationModel.Hosting` (an instance's gateway client), each with its
`docs/DESIGN.md`. Read the one a change touches before changing a half. Each pair's own decisions are
in its gateway half's `docs/DESIGN.md` (Kubernetes `GK*`, Docker `GD*`) and its instance half's
`docs/DESIGN.md`.

## The pair

| Half | Package | References | Public surface |
| --- | --- | --- | --- |
| Gateway | `Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>` | `Assimalign.Cohesion.ApplicationModel.Gateway`, plus the platform's own client library if it needs one (`KubernetesClient`) | `<Platform>Infrastructure`, its options, `builder.Use<Platform>(infrastructure)` on the gateway builder, and the platform's resource-extension verbs (`WithReplicas`, `WithDockerReplicas`) |
| Instance | `Assimalign.Cohesion.ApplicationModel.Hosting.<Platform>` | `Assimalign.Cohesion.ApplicationModel.Hosting`, plus the same platform client if needed | `options.Use<Platform>()`, an extension on `TOptions : ApplicationInstanceOptions` that adds the platform's gateway client to a resource's program: `builder.Services.AddResource(name, options => options.Use<Platform>())` |

- **The gateway half is one type for both seams.** `<Platform>Infrastructure` implements
  `IApplicationGatewayInfrastructureProvider` (plan, deploy, watch, outputs, teardown, and, from its
  `StartAsync(context)`/`StopAsync`, the inbound channel that hands publications and renewals to
  `context.Publisher`) and `IApplicationGatewayInstanceClient` (calls into an instance's control
  plane). `Use<Platform>` sets it as both. `AddImage(provider, name, image)` binds a resource to the
  image that runs it.
- **The instance half is the other end of the channel.** It publishes and renews, and serves the
  instance's control plane. An instance picks its client by the runtime contract's
  `COHESION_GATEWAY` value, the platform's name.
- **One shared protocol file per platform.** Names, labels, variables, and endpoint forms both halves
  must agree on live in `platforms/<Platform>/Assimalign.Cohesion.ApplicationModel.Hosting.<Platform>/shared/<Platform>Protocol.cs`
  (namespace `Assimalign.Cohesion.ApplicationModel.<Platform>`, `internal` types). Both csprojs compile
  it with `<Compile Include="..." Link="Internal\<Platform>Protocol.cs" />`. A change to it changes
  both halves; test both.
- **`platforms/Shared/` is linked source, never a project.** It holds code more than one platform
  compiles, today `InstanceControlPlaneListener.cs` (namespace
  `Assimalign.Cohesion.ApplicationModel.Hosting.Platforms`), which every instance half that serves its
  control plane over HTTP links. Each package keeps its own compiled copy.

## Hard rules

1. **No references between halves or between platforms.** A shipped platform project never
   references the other half of its pair, nor any project of another platform. What two halves
   share is the protocol file; what two platforms share is `platforms/Shared/`. Test projects may
   reference the other half to run it in process (`Gateway.Docker.Tests` runs `Hosting.Docker`), and
   fixtures reference the instance half they exercise.
2. **Layering.** A gateway half references the Gateway contract package; an instance half the
   Hosting contract package; either may add the platform's own client library. Neither references a
   resource-area package (an `<Area>.ApplicationModel`, an `<Area>.Hosting` runtime module or its
   `<Area>.Hosting.ApplicationModel` integration, an `<Area>.Application`, an `<Area>.Client`) or any
   `Microsoft.Extensions.*` assembly. Area behavior never reaches a platform package: a resource's
   program composes its area and opts into the platform; the gateway sees only manifests, settings,
   and actions.
3. **The revision is what the model does not carry.** `PlanResourceAsync` returns a revision over
   what the provider deploys that the manifest does not say (template version, image, ports), never
   the settings' values or the extensions: the gateway fingerprints the manifest itself, and a new
   revision is a new definition that drops the resource's recorded action state. A separate
   **workload** hash (revision, extensions such as replicas, settings' values), stored on the
   platform's objects, decides create, update, or nothing. Bump the platform's `TemplateVersion`
   whenever what it builds changes. (Kubernetes GK5, Docker GD5.)
4. **One runtime contract.** A platform hands each instance the `COHESION_*` variables through the
   names on Core's `AppEnvironment.Variables`, plus its own platform variables from the protocol file.
   `docs/RUNTIME_CONTRACT.md` in the cohesion repository is the wire contract.
5. **The program is never PID 1.** A .NET program that is PID 1 cannot end itself on an unhandled
   start failure. Kubernetes shares the pod's process namespace; Docker runs the engine's init.
   (GK6, GD6.)
6. **Workloads outlive the gateway.** A gateway that stops leaves its workloads running; one that runs
   again over the same namespace or network adopts them, and their instances publish to it again.
   Teardown removes a resource's workloads; the namespace or network stays.
7. **A watch fails fast with the platform's reason.** Watching a deployment fails the step at once
   when one of its workloads cannot run without a change to what it runs, naming the workload and the
   platform's reason, and ends when a later deployment or a teardown replaced it.

## Enforcement

`build/Targets/Build.Rules.targets` enforces rule 2 as **COHPLT001** for every shipped project under
`platforms/**`; `tests/`, `samples/`, `examples/`, and `fixtures/` projects are excluded by path. It
checks the project-reference graph and the resolved compile and runtime assembly closure after
`ResolveAssemblyReferences`, so transitive and package-delivered assemblies count, and it names every
offending assembly. It rejects `Assimalign.Cohesion.<X>.ApplicationModel`,
`Assimalign.Cohesion.<X>.Hosting[.*]` (except `Assimalign.Cohesion.ApplicationModel.Hosting[.*]`),
`Assimalign.Cohesion.<X>.Application`, and `Microsoft.Extensions.*`. `<Area>.Client` and rule 1 are
enforced in review.

## Layout of an area

```
platforms/<Platform>/
├── README.md
├── Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>/{src,tests,docs}
├── Assimalign.Cohesion.ApplicationModel.Hosting.<Platform>/{src,tests,docs,shared}
├── fixtures/WebProgram/        resource program the end-to-end tests deploy (IsPackable=false)
└── scripts/                    builds and publishes the fixture image, and any local cluster
```

- Project, assembly, and package names are `Assimalign.Cohesion.ApplicationModel.Gateway.<Platform>`
  and `Assimalign.Cohesion.ApplicationModel.Hosting.<Platform>`, with `RootNamespace` equal to the
  assembly name. Test projects append `.Tests`; fixtures use
  `Assimalign.Cohesion.ApplicationModel.<Platform>.Fixtures`.
- The end-to-end tests live in the gateway half's tests and skip unless their environment names a
  target (`COHESION_KIND_CONTEXT`, `COHESION_DOCKER_E2E=1`).

## CI and packages

- **Per-platform workflow** `platform-<platform>.yml`: on every push touching the area,
  `platforms/Shared/`, or the build, it runs `.github/actions/build` for both halves on Linux,
  Windows, and macOS (build, then the tests).
- **End-to-end workflows** `e2e-kubernetes.yml` (kind on the runner's Docker engine) and
  `e2e-docker.yml` (the runner's Docker engine): on demand and weekly, and each fails unless its tests
  executed and passed, so a skip never reads as a pass.
- **`ci-packages.yml`** packs both halves of every pair (its `CI_PACKAGE_PROJECTS` list), after their
  tests, at a CI version of their own (`10.0.0-preview.0.ci.<run>` for `main`,
  `10.0.0-preview.0.pr.<pr>.<run>` for a same-repository pull request) and pushes them to the
  Assimalign GitHub Packages feed. It never publishes to nuget.org or a canonical version.
- **The cohesion pin.** Every Cohesion package is consumed at exactly `CohesionPackageVersion` in
  `build/Targets/Build.References.Packages.targets`: a cohesion CI build from its own
  `ci-packages.yml`. The platform packages depend on exactly that build, so a consumer pins one build
  of each repository. A pull request's cohesion CI versions are deleted when it closes; move the pin
  to a `main` build once it merges. Locally, `.github/scripts/Pack-CohesionSibling.ps1 -Local` packs
  the sibling cohesion checkout, and `-p:CohesionSiblingPackageVersion=<version>` selects it.

## Adding a platform

1. Both halves, with `{src,tests,docs}`, the shared protocol file, and a `README.md` for the area.
2. `.slnx` entries for every project and file.
3. `platform-<platform>.yml` with both halves in its matrix and `platforms/Shared/**` in its paths.
4. Both halves in `ci-packages.yml`'s `CI_PACKAGE_PROJECTS`.
5. A fixture program and an `e2e-<platform>.yml` once the platform runs end to end.
6. The root `README.md` table and `.claude/CLAUDE.md` repository map.

## AOT posture

**No hard AOT mandate in this repo** (owner decision, 2026-07-20): gateways are deploy-time control
planes, so `KubernetesClient` and similar dependencies need no exception machinery. The cohesion
repo's deployed runtime libraries retain their AOT mandate. Keep source-generated serialization as
the default. Reintroducing a repo-wide mandate requires explicit owner confirmation under
`deviations.md`.

## Changing these rules

Changing a hard rule means updating this file, every affected area `README.md` and `DESIGN.md`, and
any build enforcement in the same commit, with the owner's confirmation under `deviations.md`.
