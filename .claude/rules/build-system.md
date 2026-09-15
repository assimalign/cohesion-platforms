---
paths:
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
  - "**/*.slnx"
  - "global.json"
  - "nuget.config"
  - "build/**"
  - "sdks/**"
  - ".github/workflows/**"
---

# Build System

This repo uses the same centralized-MSBuild model as the cohesion repo, minus cohesion's shared-framework/installer machinery. The full model (SDK + shared-framework packaging, `Install-Local.ps1`, framework packs) is documented in the **cohesion** repo's `.claude/rules/build-system.md` — consult it when consuming those SDKs; this file covers what applies *here*.

## Centralized MSBuild logic

- Root `Directory.Build.props`/`.targets` are two-line shims importing `build/Build.props` and `build/Build.targets`, which import the `build/Targets/*.props|targets` chain (Global, TargetFramework, Branding, Version, Constants, References, Rules).
- Shared build logic lives in `build/Targets/` — lift any block shared by 2+ sibling csprojs into the chain. Library csprojs should stay under ~10 lines.
- **Versioning:** `$(CohesionVersion)` in `build/Targets/Build.Version.props` is the single source of truth for this repository's own package outputs (major derived from the TFM; currently `10.0.1-preview.3`). The consumed Cohesion dependency floor is separate and lives in `Build.References.Packages.targets`. Per-project `<Version>` overrides are forbidden; advance the output identity rather than replacing a published package.
- **Target framework:** `TargetFrameworkLatest` (`net10.0`) in `build/Targets/Build.TargetFramework.props`; SDK pinned in `global.json` — keep both in lockstep with the cohesion repo.

## References

- **Internal project references** use name-only items resolved by `build/Targets/Build.References.Projects.targets` (globbing `platforms/**/*.csproj`):
  ```xml
  <CohesionProjectReference Include="Assimalign.Cohesion.ApplicationModel.Gateway.Containers" />
  ```
  `CohesionPrivateProjectReference` is the `PrivateAssets=all` variant. Raw `ProjectReference` with relative paths is forbidden.
- **NuGet packages** — including the `Assimalign.Cohesion.*` packages from the cohesion repo — use `CohesionPackageReference`, with the version declared centrally in `build/Targets/Build.References.Packages.targets` first:
  ```xml
  <CohesionPackageReference Include="Assimalign.Cohesion.ApplicationModel.Gateway" />
  ```
  Raw `PackageReference` is forbidden. `KubernetesClient` is already pinned centrally.

## Consuming cohesion packages

- `nuget.config` keeps nuget.org as the public source; the build appends a sibling Cohesion feed
  when present, and CI adds the authenticated Assimalign GitHub Packages source
  (`https://nuget.pkg.github.com/assimalign/index.json`).
- Published versions are **immutable per release**. The three central Cohesion pins use the release
  floor `[10.0.1-preview.3, )`; advance that floor to consume a newer Cohesion line, and never pin
  an identity that is absent from the configured feed.
- Inner-loop builds automatically append the sibling checkout's local feed
  (`../cohesion/_out/packages`, populated by `installer/scripts/Install-Local.ps1`). After packing a
  complete local closure, pass the overridable `CohesionSiblingPackageVersion` property to select
  the exact local identity `10.0.1-preview.3.local`. The `.local` prerelease sorts above the
  canonical prerelease while remaining a distinct, never-published package identity.
- CI has no sibling checkout, so it uses the release floor against GitHub Packages. Do not add a
  local credential block or check credentials into `nuget.config`.
- Never hardcode a version on `<Import Sdk>` elements; msbuild-sdks are pinned in `global.json`.

## Rules & CI

- `build/Targets/Build.Rules.targets` enforces platform layering as **COHPLT001** for shipped
  projects under `platforms/**` (tests, samples, and examples excluded by path). It checks both the
  project-reference graph and the resolved assembly closure after `ResolveAssemblyReferences`, so
  package-delivered forbidden DLLs count, and reports every offending assembly. The normative
  allowlist and forbidden families are in `platform-areas.md` rule 3.
- If a platform rule becomes build-enforced or its enforcement changes, update
  `Build.Rules.targets`, `platform-areas.md`, and every affected area README in the same commit.
- CI mirrors cohesion: a composite build action (`.github/actions/build`) building `<area>/<Category>/<Project>/src` and testing `<...>/tests` across a 3-OS matrix, invoked by thin path-filtered per-area workflows (`platform-*.yml`); nupkg publication to GitHub Packages is gated on `main` + Linux.
