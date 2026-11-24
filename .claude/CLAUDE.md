# cohesion-platforms

This repository is the **deployment plane** for [Cohesion](https://github.com/assimalign/cohesion), Assimalign's code-first, multi-service application framework for .NET. It will hold each platform's fabric contract package and its drivers, `Assimalign.Cohesion.Fabric.<Platform>` and `Assimalign.Cohesion.Fabric.<Platform>.Drivers`. Those packages are planned for a later stage. See `../cohesion/.claude/rules/fabric.md` and `../cohesion/fabric/README.md`.

- The build targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`), with the .NET SDK pinned in `global.json`.
- **AOT:** This repository carries no `IsAotCompatible` mandate (owner decision, 2026-07-20). Source-generated serialization stays the default. See [General Rules](rules/general-rules.md#aot-posture).
- **No `Microsoft.Extensions.*` packages**. This is a standing architectural commitment inherited from cohesion.
- The canonical coding standard lives in `.claude/rules/` and auto-loads when matching files are touched. See [Platform Areas](rules/platform-areas.md) for platform layering.

## Relationship to the cohesion repo

- Sibling checkout `C:\Source\repos\assimalign\cohesion`.
- This repo consumes `Assimalign.Cohesion.*` packages at the cohesion CI build pinned by `CohesionPackageVersion` in `build/Targets/Build.References.Packages.targets`. For local development, `pwsh -NoProfile -File .github/scripts/Pack-CohesionSibling.ps1 -Local` packs the sibling checkout into `../cohesion/_out/packages`, and `-p:CohesionSiblingPackageVersion=10.0.0-preview.1.local` selects it. See [Build System](rules/build-system.md).
- `ci-packages.yml` publishes nothing until the fabric platform packages land. Its pack and publish jobs skip manual runs.
- Work items live in the shared org GitHub Project #13 "Cohesion" under program root `L04.01`, as issues on `assimalign/cohesion-platforms` with the project `Codebase` field set to `cohesion-platforms`. See the [cohesion-work-items skill](skills/cohesion-work-items/SKILL.md).

## Repository map

- `platforms/` holds the shared MSBuild defaults. The platform projects will land in a later stage.
- `build/` holds centralized MSBuild, package pins, and the COHPLT001 layering guard.
- `.github/` holds the composite build action, `ci-packages.yml`, and `credential-guard.yml`.
- [`scripts/New-CohesionKindCluster.ps1`](../scripts/New-CohesionKindCluster.ps1) creates a local kind cluster and registry using Podman by default.
- [`docs/PLATFORMS_PROGRAM_PLAN.md`](../docs/PLATFORMS_PROGRAM_PLAN.md) tracks the program's scope, sequencing, and decisions.
