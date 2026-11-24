# Platform Areas (`platforms/**`)

This repository will hold each platform's fabric contract package and its drivers,
`Assimalign.Cohesion.Fabric.<Platform>` and `Assimalign.Cohesion.Fabric.<Platform>.Drivers`.
Those packages are planned for a later stage. See `../cohesion/.claude/rules/fabric.md`.

## Layering

Do not reference `Microsoft.Extensions.*` packages.

`build/Targets/Build.Rules.targets` enforces **COHPLT001** for shipped projects under
`platforms/**`. It excludes `tests/`, `samples/`, `examples/`, and `fixtures/` by path.
It checks the project-reference graph and the resolved compile and runtime assembly closure
after `ResolveAssemblyReferences`, including transitive and package-delivered assemblies.
It names every offending assembly.

COHPLT001 rejects `Assimalign.Cohesion.<X>.ApplicationModel`,
`Assimalign.Cohesion.<X>.Hosting[.*]`, `Assimalign.Cohesion.<X>.Application`, and
`Microsoft.Extensions.*`.

## CI and packages

- **`ci-packages.yml`** publishes nothing until the fabric platform packages exist. It has
  only a manual trigger for now, and its pack and publish jobs skip manual runs. The
  publisher targets Assimalign GitHub Packages and uses CI versions (`10.0.0-preview.0.ci.<run>`
  or `10.0.0-preview.0.pr.<pr>.<run>`).
- **The cohesion pin.** `CohesionPackageVersion` in
  `build/Targets/Build.References.Packages.targets` pins a single cohesion CI build from its
  own `ci-packages.yml`. A pull request's cohesion CI versions are deleted when it closes.
  Move the pin to a `main` build once it merges. Locally,
  `.github/scripts/Pack-CohesionSibling.ps1 -Local` packs the sibling cohesion checkout,
  and `-p:CohesionSiblingPackageVersion=<version>` selects it.

## AOT posture

**No hard AOT mandate in this repo** (owner decision, 2026-07-20): gateways are deploy-time control
planes, so `KubernetesClient` and similar dependencies need no exception machinery. The cohesion
repo's deployed runtime libraries retain their AOT mandate. Keep source-generated serialization as
the default. Reintroducing a repo-wide mandate requires explicit owner confirmation under
`deviations.md`.

## Changing these rules

Changing a hard rule means updating this file, every affected area `README.md` and `DESIGN.md`, and
any build enforcement in the same commit, with the owner's confirmation under `deviations.md`.
