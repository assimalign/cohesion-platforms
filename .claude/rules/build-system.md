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
- **Versioning:** `$(CohesionVersion)` in `build/Targets/Build.Version.props` is the single source of truth (major derived from the TFM). Per-project `<Version>` overrides are forbidden. Keep the version aligned with the cohesion packages this repo consumes.
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

- `nuget.config` maps `Assimalign.Cohesion.*` via `packageSourceMapping` to the Assimalign GitHub Packages feed (`https://nuget.pkg.github.com/assimalign/index.json`); everything else resolves from nuget.org.
- GitHub Packages is a **QA/UAT staging feed with delete-then-replace semantics**: the cohesion repo republishes the *same* version (e.g., `10.0.1-preview.2`) on every `main` push. Restore with `--no-cache` (or clear the local package cache) when picking up refreshed upstream bits.
- Inner-loop against unpublished cohesion changes: add the sibling checkout's local feed (`../cohesion/_out/packages`, populated by cohesion's `Install-Local.ps1` / `dotnet pack`) as a higher-priority source in a local, uncommitted `nuget.config` override.
- Never hardcode a version on `<Import Sdk>` elements; msbuild-sdks are pinned in `global.json`.

## Rules & CI

- `build/Targets/Build.Rules.targets` carries build-enforced architecture rules (the `COHRES*` diagnostics). Platform-area rules live in `platform-areas.md`; if a rule becomes build-enforced, the enforcement lands in `Build.Rules.targets` and both files change in the same commit.
- CI mirrors cohesion: a composite build action (`.github/actions/build`) building `<area>/<Category>/<Project>/src` and testing `<...>/tests` across a 3-OS matrix, invoked by thin path-filtered per-area workflows (`platform-*.yml`); nupkg publish to GitHub Packages is gated on `main` + Linux and uses `.github/scripts/Publish-Nupkg.ps1` (delete-then-push).
