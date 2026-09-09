# Cohesion Platforms

The **deployment/hosting plane** for [Cohesion](https://github.com/assimalign/cohesion) —
Assimalign's code-first, multi-service application framework for .NET.

Cohesion applications are described once as a desired-state graph of resources
(`Assimalign.Cohesion.ApplicationModel`) and *realized* by a pluggable gateway
(`Assimalign.Cohesion.ApplicationModel.Gateway`): the same application runs as local child
processes, Docker containers, or Kubernetes workloads without changing application code. The
cohesion repo ships the contracts, the guided gateway base, and the `LocalGateway`; **this repo
implements the platform gateways**:

At application `Build()`, each resource-area planner produces a validated, platform-neutral
`ResourcePlan` (`cohesion/plan/v1`). Exactly one compiler for the selected platform translates
those plans into platform objects; compilers never dispatch on resource kind, area, or CLR type.

| Area | Package | Target |
| --- | --- | --- |
| [`platforms/Containers`](platforms/Containers/README.md) | `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` | Shared container machinery (image index, OCI store, registry, test primitives) |
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

The three centrally managed Cohesion dependencies use the release floor
`[10.0.1-preview.3, )`. Published package identities are immutable per release: bump that floor
to pick up a newer Cohesion line, and never pin a version that is absent from the configured feed.

Restore resolves the floor or an explicit inner-loop identity from one of two places (see
`nuget.config`):

1. **Sibling checkout (inner loop):** if `../cohesion/_out/packages` exists next to this repo, it
   is appended automatically. Populate a complete local package set from the cohesion checkout
   with `pwsh installer/scripts/Install-Local.ps1`, then select its exact identity with
   `-p:CohesionSiblingPackageVersion=10.0.1-preview.3.local`. The property is honored only while
   the sibling feed exists and is intentionally overridable for the next local line. The `.local`
   prerelease sorts above the canonical prerelease; the exact sibling override therefore makes
   the floor resolve to the inner-loop pack when both identities are present.
2. **GitHub Packages:** `https://nuget.pkg.github.com/assimalign/index.json` is the immutable
   release/staging source used by CI, where no sibling checkout exists. Local authenticated use
   requires a token with `read:packages`; keep credentials outside this repository.

## Repository conventions

The canonical coding standard lives in `.claude/rules/` (mirrored from the cohesion repo;
`platform-areas.md` is this repo's core architecture rule). Work items are managed with the
`cohesion-work-items` skill (`.claude/skills/cohesion-work-items/`).
