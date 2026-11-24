---
paths:
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
  - "**/*.slnx"
  - "global.json"
  - "nuget.config"
  - "build/**"
  - ".github/workflows/**"
---

# Build System

This repo uses the same centralized-MSBuild model as the cohesion repo, minus cohesion's SDK, shared-framework, and release machinery (documented in the **cohesion** repo's `.claude/rules/build-system.md`). This file covers what applies *here*.

## Centralized MSBuild logic

- Root `Directory.Build.props`/`.targets` are two-line shims importing `build/Build.props` and `build/Build.targets`, which import the `build/Targets/*.props|targets` chain (Global, TargetFramework, Branding, Version, Constants, References, Rules).
- Shared build logic lives in `build/Targets/` — lift any block shared by 2+ sibling csprojs into the chain. Library csprojs should stay under ~10 lines.
- **Versioning:** `$(CohesionVersion)` in `build/Targets/Build.Version.props` is the single source of truth for this repository's own package outputs (major derived from the TFM; currently `10.0.0-preview.1`). The consumed Cohesion version is separate and lives in `Build.References.Packages.targets` ("Consuming cohesion packages"). Per-project `<Version>` overrides are forbidden; advance the output identity rather than replacing a published package.
- **Target framework:** `TargetFrameworkLatest` (`net10.0`) in `build/Targets/Build.TargetFramework.props`; SDK pinned in `global.json` — keep both in lockstep with the cohesion repo.

## References

- **Internal project references** use name-only items resolved by
  `build/Targets/Build.References.Projects.targets` (globbing `platforms/**/*.csproj`).
  `CohesionPrivateProjectReference` is the `PrivateAssets=all` variant.
  Raw `ProjectReference` with relative paths is forbidden.
- **NuGet packages**, including `Assimalign.Cohesion.*` packages from the cohesion repo,
  use `CohesionPackageReference`, with the version declared centrally in
  `build/Targets/Build.References.Packages.targets` first.
  ```xml
  <CohesionPackageReference Include="Assimalign.Cohesion.Core" />
  ```
  Raw `PackageReference` is forbidden. `KubernetesClient` is already pinned centrally.

## Consuming cohesion packages

- Every `Assimalign.Cohesion.*` package is consumed at exactly one version, chosen in
  `Build.References.Packages.targets`, in this order:
  1. `CohesionSiblingPackageVersion`, when the sibling feed `../cohesion/_out/packages` exists: the
     inner loop. `pwsh -NoProfile -File .github/scripts/Pack-CohesionSibling.ps1 -Local` packs the
     sibling checkout as it is and prints the version (`10.0.0-preview.1.local`); restore with
     `-p:CohesionSiblingPackageVersion=<that version>`.
  2. `CohesionPackageVersion`: a cohesion CI build from cohesion's `ci-packages.yml`
     (`10.0.0-preview.0.ci.<run>` for its `main`, `10.0.0-preview.0.pr.<pr>.<run>` for a pull
     request) on the GitHub Packages feed. CI uses this pin. A pull request's versions are deleted
     when it closes, so move the pin to a `main` build once the pull request merges.
  3. The release floor `[10.0.0-preview.1, )`, when `CohesionPackageVersion` is set empty.
- `nuget.config` keeps nuget.org as the public source; the build appends the sibling feed when it
  exists, and CI adds the authenticated Assimalign GitHub Packages source
  (`https://nuget.pkg.github.com/assimalign/index.json`). Locally that feed needs a token with
  `read:packages`. Never check credentials into `nuget.config`; `credential-guard.yml` fails a push
  that does.
- Never hardcode a version on `<Import Sdk>` elements; msbuild-sdks are pinned in `global.json`.

## Rules & CI

- `build/Targets/Build.Rules.targets` enforces platform layering as **COHPLT001** for shipped
  projects under `platforms/**` (tests, samples, examples, and fixtures excluded by path). It checks
  both the project-reference graph and the resolved assembly closure after
  `ResolveAssemblyReferences`, so package-delivered forbidden DLLs count, and reports every offending
  assembly. See [Platform Areas](platform-areas.md#layering).
- If a platform rule becomes build-enforced or its enforcement changes, update
  `Build.Rules.targets`, `platform-areas.md`, and every affected area README in the same commit.
- The composite build action (`.github/actions/build`) builds and tests one project.
  Its `cohesion-ref` input builds against a sibling cohesion checkout at a ref, packed
  at cohesion's `.local` version and passed to restore as `CohesionSiblingPackageVersion`.
- `ci-packages.yml` is the only publisher. It publishes nothing until the fabric platform
  packages land. It has only a manual trigger for now, and its pack and publish jobs skip
  manual runs. See [Platform Areas](platform-areas.md#ci-and-packages).
