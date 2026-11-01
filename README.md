# Cohesion Platforms

The **deployment plane** for [Cohesion](https://github.com/assimalign/cohesion) — Assimalign's
code-first, multi-service application framework for .NET.

A Cohesion application describes its resources once, as an application model, and a gateway deploys
them onto a platform: the same resource programs run in process, as Docker containers, or as
Kubernetes workloads. The cohesion repo ships the model, the gateway and instance contracts
(`Assimalign.Cohesion.ApplicationModel.Gateway`, `Assimalign.Cohesion.ApplicationModel.Hosting`), and
the in-process platform; **this repo implements the other platforms**.

Each platform is a pair of packages: a gateway half the gateway program uses
(`builder.UseKubernetes(...)`, `builder.UseDocker(...)`), and an instance half each resource's program
opts into on its `ApplicationInstanceOptions` (`builder.Services.AddResource(name, options => options.UseKubernetes())`, or `options.UseDocker()`). The two halves share one protocol
file and never reference each other.

| Area | Packages | Target |
| --- | --- | --- |
| [`platforms/Kubernetes`](platforms/Kubernetes/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`, `Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes` | Kubernetes clusters (kind on Podman for development) |
| [`platforms/Docker`](platforms/Docker/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`, `Assimalign.Cohesion.ApplicationModel.Hosting.Docker` | Docker-compatible engines (Podman locally) |

Delivery is tracked in the shared org [Project #13 "Cohesion"](https://github.com/orgs/assimalign/projects/13)
under WBS program root `L04.01` (`Codebase = cohesion-platforms`); what each platform does today and
what comes next is in [docs/PLATFORMS_PROGRAM_PLAN.md](docs/PLATFORMS_PROGRAM_PLAN.md).

## Building

```pwsh
dotnet build Assimalign.Cohesion.Platforms.slnx
dotnet test platforms/<Area>/<Project>/tests/
```

- .NET SDK is pinned in `global.json`; everything targets `net10.0` with `LangVersion=Preview`.
  Unlike the cohesion repo — whose libraries are hard-required to be AOT-compatible because they
  are the deployed, performance-critical runtimes — the platform packages carry **no AOT
  mandate**: their gateway halves are deploy-time control planes. Source-generated serialization
  remains the default for hygiene.
- The build system mirrors the cohesion repo: centralized MSBuild under `build/Targets/`,
  name-only `CohesionProjectReference` items, centrally pinned `CohesionPackageReference`
  versions, and a single `$(CohesionVersion)`.

### Getting the `Assimalign.Cohesion.*` packages

Every Cohesion package is consumed at one version (`build/Targets/Build.References.Packages.targets`):

1. **A cohesion CI build** (CI and the default): `CohesionPackageVersion` pins a version cohesion's
   `ci-packages.yml` published to `https://nuget.pkg.github.com/assimalign/index.json`
   (`10.0.0-preview.0.ci.<run>` for its `main`, `10.0.0-preview.0.pr.<pr>.<run>` for a pull
   request). Local use of the feed needs a token with `read:packages`; keep credentials outside this
   repository.
2. **The sibling checkout** (inner loop): `pwsh -NoProfile -File .github/scripts/Pack-CohesionSibling.ps1 -Local`
   packs `../cohesion` as it is into its `_out/packages`, which restore appends automatically, and
   `-p:CohesionSiblingPackageVersion=10.0.0-preview.1.local` selects it.

`ci-packages.yml` publishes this repository's packages the same way, at a CI version of their own
that depends on exactly the pinned cohesion build, so a consumer pins one build of each repository.

## Repository conventions

The canonical coding standard lives in `.claude/rules/` (mirrored from the cohesion repo;
`platform-areas.md` is this repo's core architecture rule). Work items are managed with the
`cohesion-work-items` skill (`.claude/skills/cohesion-work-items/`).
