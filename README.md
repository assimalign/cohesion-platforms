# Cohesion Platforms

The **deployment plane** for [Cohesion](https://github.com/assimalign/cohesion), Assimalign's
code-first, multi-service application framework for .NET.

This repository will hold each platform's fabric contract package and its drivers,
`Assimalign.Cohesion.Fabric.<Platform>` and `Assimalign.Cohesion.Fabric.<Platform>.Drivers`.
Those packages are planned for a later stage. See `../cohesion/.claude/rules/fabric.md`.

Delivery is tracked in the shared org [Project #13 "Cohesion"](https://github.com/orgs/assimalign/projects/13)
under WBS program root `L04.01` (`Codebase = cohesion-platforms`). See the
[platforms program plan](docs/PLATFORMS_PROGRAM_PLAN.md) for scope and sequencing.

## Build configuration

- The .NET SDK is pinned in `global.json`. The build targets `net10.0` with `LangVersion=Preview`.
- The platform packages carry **no AOT mandate**. Source-generated serialization remains the default.
- The build system mirrors the cohesion repo with centralized MSBuild under `build/Targets/`,
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

`ci-packages.yml` publishes nothing until the fabric platform packages land. It has only a manual
trigger for now, and its pack and publish jobs skip manual runs.

## Local Kubernetes development

[`scripts/New-CohesionKindCluster.ps1`](scripts/New-CohesionKindCluster.ps1) creates a kind cluster
using Podman by default and configures its nodes to pull from a local registry at `localhost:5001`.
On Windows, install `kind` (`winget install Kubernetes.kind` or Podman Desktop's Kind extension)
and `kubectl`, and use a running rootful Podman machine.

```powershell
pwsh -NoProfile -File scripts/New-CohesionKindCluster.ps1
```

Pass `-Recreate` to recreate the cluster or `-Engine docker` to use a Docker engine.

## Repository conventions

The canonical coding standard lives in `.claude/rules/` (mirrored from the cohesion repo;
`platform-areas.md` is this repo's core architecture rule). Work items are managed with the
`cohesion-work-items` skill (`.claude/skills/cohesion-work-items/`).
