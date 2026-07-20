# cohesion-platforms

This repository is the **deployment/hosting plane** for [Cohesion](https://github.com/assimalign/cohesion) — Assimalign's code-first, multi-service application framework for .NET. Where the cohesion repo defines the application model (`Assimalign.Cohesion.ApplicationModel`) and the gateway contract + guided base (`Assimalign.Cohesion.ApplicationModel.Gateway`), **this repo implements those contracts for concrete platforms**: a gateway realizes an `IApplicationModel` (an immutable desired-state graph of resources) onto a deployment target — Kubernetes and Docker here, with `LocalGateway` (child processes) remaining in the cohesion repo.

- Everything targets `net10.0` (`LangVersion=Preview`, `EnablePreviewFeatures=true`), .NET SDK pinned in `global.json`.
- **NativeAOT compatibility is a standing requirement** (`IsAotCompatible=true`). Sanctioned exception: the Kubernetes gateway (`KubernetesClient` is not trim-safe) — see `.claude/rules/general-rules.md`.
- **No `Microsoft.Extensions.*` packages** — standing architectural commitment inherited from cohesion.
- The canonical coding standard lives in `.claude/rules/` and auto-loads when matching files are touched. `platform-areas.md` is this repo's core architecture rule.

## Relationship to the cohesion repo

- Sibling checkout: `C:\Source\repos\assimalign\cohesion`. The authoritative gateway/application-model design is `libraries/ApplicationModel/DESIGN.md` there (v2.x) — read it before changing gateway behavior.
- This repo **consumes** `Assimalign.Cohesion.*` packages (ApplicationModel, Gateway, Core, Http stack, …) as NuGet packages — GitHub Packages (`https://nuget.pkg.github.com/assimalign/index.json`) is the staging feed; a local sibling-checkout feed (`../cohesion/_out/packages`) serves inner-loop development.
- Work items live in the shared org GitHub Project #13 "Cohesion" under program root `L04.01`, as issues on `assimalign/cohesion-platforms` with the project `Codebase` field set to `cohesion-platforms`. Use the `cohesion-work-items` skill.

## Repository map

- `platforms/` — one folder per deployment target, each an area with `Assimalign.Cohesion.*` projects in `{src,tests,docs}` layout:
  - `platforms/Containers/` — shared container-gateway infrastructure (image index/artifacts, OCI store, embedded registry, image-gather build seam)
  - `platforms/Kubernetes/` — `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`
  - `platforms/Docker/` — `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`
- `sdks/` — MSBuild SDK/targets packages this repo ships (e.g., orchestrator-side container gathering targets)
- `build/` — centralized MSBuild (`build/Targets/*.props|targets`); same `CohesionProjectReference` / `CohesionPackageReference` / `$(CohesionVersion)` model as cohesion — see `.claude/rules/build-system.md`
- `docs/` — repo-level docs, including `PLATFORMS_PROGRAM_PLAN.md` (the multi-session delivery plan)

## Build & test

```pwsh
dotnet build                        # whole repo
dotnet test platforms/<Area>/<Project>/tests/
```

Local end-to-end validation uses **Kind running on Podman** (`KIND_EXPERIMENTAL_PROVIDER=podman`) for Kubernetes and the Podman Docker-compatible socket for Docker.
