# Cohesion Platforms

The **deployment/hosting plane** for [Cohesion](https://github.com/assimalign/cohesion) —
Assimalign's code-first, multi-service application framework for .NET.

Cohesion applications are described once as a desired-state graph of resources
(`Assimalign.Cohesion.ApplicationModel`) and *realized* by a pluggable gateway
(`Assimalign.Cohesion.ApplicationModel.Gateway`): the same application runs as local child
processes, Docker containers, or Kubernetes workloads without changing application code. The
cohesion repo ships the contracts, the guided gateway base, and the `LocalGateway`; **this repo
implements the platform gateways**:

| Area | Package | Target |
| --- | --- | --- |
| [`platforms/Containers`](platforms/Containers/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` | Shared container machinery (image index, OCI store, state manager, registry) |
| [`platforms/Kubernetes`](platforms/Kubernetes/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` | Kubernetes clusters (Kind on Podman for development) |
| [`platforms/Docker`](platforms/Docker/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Docker` | Docker-compatible engines (Podman locally) |

Delivery is tracked in the shared org [Project #13 "Cohesion"](https://github.com/orgs/assimalign/projects/13)
under WBS program root `L04.01` (`Codebase = cohesion-platforms`); sequencing lives in
[docs/PLATFORMS_PROGRAM_PLAN.md](docs/PLATFORMS_PROGRAM_PLAN.md).

## Building

```pwsh
dotnet build Assimalign.Cohesion.Platforms.slnx
dotnet test platforms/<Area>/<Project>/tests/
```

- .NET SDK is pinned in `global.json`; everything targets `net10.0` with `LangVersion=Preview`.
  Unlike the cohesion repo — whose libraries are hard-required to be AOT-compatible because they
  are the deployed, performance-critical runtimes — the platform gateways carry **no AOT
  mandate**: they are deploy-time control planes. Source-generated serialization remains the
  default for hygiene.
- The build system mirrors the cohesion repo: centralized MSBuild under `build/Targets/`,
  name-only `CohesionProjectReference` items, centrally pinned `CohesionPackageReference`
  versions, and a single `$(CohesionVersion)`.

### Getting the `Assimalign.Cohesion.*` packages

Restore resolves them from one of two places (see `nuget.config`):

1. **Sibling checkout (inner loop, automatic):** if `../cohesion/_out/packages` exists next to
   this repo, it is appended as a restore source. Populate it from the cohesion checkout with
   `dotnet pack` (or `pwsh installer/scripts/Install-Local.ps1`).
2. **GitHub Packages staging feed:** `https://nuget.pkg.github.com/assimalign/index.json` — added
   automatically in CI; locally add it with a token that has `read:packages`. The feed uses
   delete-then-replace semantics on a constant version, so use a no-cache restore when picking up
   refreshed upstream bits.

## Repository conventions

The canonical coding standard lives in `.claude/rules/` (mirrored from the cohesion repo;
`platform-areas.md` is this repo's core architecture rule). Work items are managed with the
`cohesion-work-items` skill (`.claude/skills/cohesion-work-items/`).
