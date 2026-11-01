# cohesion-platforms

This repository is the **deployment plane** for [Cohesion](https://github.com/assimalign/cohesion) — Assimalign's code-first, multi-service application framework for .NET. The cohesion repo defines the application model and its two contracts: the gateway's (`Assimalign.Cohesion.ApplicationModel.Gateway`: the infrastructure provider and instance client a platform implements) and the instance's (`Assimalign.Cohesion.ApplicationModel.Hosting`: the gateway client a resource's program uses). **This repo implements those contracts for concrete platforms, one platform pair each**: a gateway half that deploys a gateway's resources onto the platform, and an instance half each resource's program opts into. The in-process platform pair stays in the cohesion repo.

- Everything targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`), .NET SDK pinned in `global.json`.
- **AOT: unlike the cohesion repo, this repo carries no `IsAotCompatible` mandate** (owner decision, 2026-07-20) — gateways are deploy-time control planes; the cohesion libraries (the deployed runtimes) keep the hard requirement. Source-generated serialization stays the default. See `.claude/rules/general-rules.md § AOT posture`.
- **No `Microsoft.Extensions.*` packages** — standing architectural commitment inherited from cohesion.
- The canonical coding standard lives in `.claude/rules/` and auto-loads when matching files are touched. `platform-areas.md` is this repo's core architecture rule.

## Relationship to the cohesion repo

- Sibling checkout: `C:\Source\repos\assimalign\cohesion`. Before changing a half, read the contract it implements: `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md` for a gateway half, `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Hosting/docs/DESIGN.md` for an instance half. `docs/RUNTIME_CONTRACT.md` there is the wire contract a platform hands each instance.
- This repo **consumes** `Assimalign.Cohesion.*` packages at exactly one cohesion CI build, `CohesionPackageVersion` in `build/Targets/Build.References.Packages.targets`, from GitHub Packages (`https://nuget.pkg.github.com/assimalign/index.json`). For the inner loop, `pwsh -NoProfile -File .github/scripts/Pack-CohesionSibling.ps1 -Local` packs the sibling checkout into `../cohesion/_out/packages`, and `-p:CohesionSiblingPackageVersion=10.0.0-preview.1.local` selects it. See `.claude/rules/build-system.md`.
- `ci-packages.yml` publishes both halves of every pair to GitHub Packages at a CI version (`10.0.0-preview.0.ci.<run>`, or `.pr.<pr>.<run>`) that depends on exactly the pinned cohesion build.
- Work items live in the shared org GitHub Project #13 "Cohesion" under program root `L04.01`, as issues on `assimalign/cohesion-platforms` with the project `Codebase` field set to `cohesion-platforms`. Use the `cohesion-work-items` skill.

## Repository map

- `platforms/` — one folder per platform, each a pair of `Assimalign.Cohesion.*` projects in `{src,tests,docs}` layout:
  - `platforms/Kubernetes/` — `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` (gateway half) and `Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes` (instance half; `shared/KubernetesProtocol.cs` is linked into both), plus `fixtures/WebProgram` and the kind `scripts/`
  - `platforms/Docker/` — `Assimalign.Cohesion.ApplicationModel.Gateway.Docker` and `Assimalign.Cohesion.ApplicationModel.Hosting.Docker` (`shared/DockerProtocol.cs` linked into both), plus `fixtures/WebProgram` and `scripts/Publish-CohesionDockerFixtures.ps1`
  - `platforms/Shared/` — linked source compiled into more than one platform package (`InstanceControlPlaneListener.cs`, the instance halves' control-plane listener); never a project, so no platform package references another
- `build/` — centralized MSBuild (`build/Targets/*.props|targets`); same `CohesionProjectReference` / `CohesionPackageReference` / `$(CohesionVersion)` model as cohesion, plus the COHPLT001 layering guard — see `.claude/rules/build-system.md`
- `.github/` — the composite build action, one `platform-<platform>.yml` and one `e2e-<platform>.yml` per platform, `ci-packages.yml`, and `credential-guard.yml`
- `docs/` — repo-level docs: `PLATFORMS_PROGRAM_PLAN.md` (what each platform does today, what comes next, and the cross-repository dependencies)

## Build & test

```pwsh
dotnet build Assimalign.Cohesion.Platforms.slnx -p:CohesionSiblingPackageVersion=10.0.0-preview.1.local   # whole repo, against the sibling pack
dotnet test platforms/<Platform>/<Project>/tests/
```

End-to-end tests live in each gateway half's tests and skip unless their target is named: kind on rootful Podman for Kubernetes (`COHESION_KIND_CONTEXT`, `platforms/Kubernetes/README.md`), and a Docker-compatible engine, Podman's included, for Docker (`COHESION_DOCKER_E2E=1`, `platforms/Docker/README.md`).
