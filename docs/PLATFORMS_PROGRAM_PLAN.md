# Deployment Platforms Program Plan (`L04.01`)

## Where the program stands

**2026-11-24.** The Docker and Kubernetes gateway/instance pairs were removed with cohesion's
old application model. Each platform's fabric contract package and its drivers
(`Assimalign.Cohesion.Fabric.<Platform>` and `Assimalign.Cohesion.Fabric.<Platform>.Drivers`)
will land in a later stage. See `../cohesion/.claude/rules/fabric.md`.

## Work items

Program root **[#1](https://github.com/assimalign/cohesion-platforms/issues/1) `[L04.01.00] Cohesion - Deployment Platforms`**
in the shared org Project #13, with `Codebase = cohesion-platforms`. The area epics are
Delivery (#2), Kubernetes (#14), and Docker (#23). Features filed before 2026-10-27 describe
the first design. See the [cohesion-work-items skill](../.claude/skills/cohesion-work-items/SKILL.md).

## Cross-repository dependencies

- `CohesionPackageVersion` in
  [`Build.References.Packages.targets`](../build/Targets/Build.References.Packages.targets)
  pins the cohesion CI build. See [Build System](../.claude/rules/build-system.md#consuming-cohesion-packages)
  for the package feed and sibling checkout override.
- `ci-packages.yml` publishes nothing until the fabric platform packages exist.

## Local development

[`scripts/New-CohesionKindCluster.ps1`](../scripts/New-CohesionKindCluster.ps1) creates a kind
cluster and local registry. See the [local Kubernetes setup](../README.md#local-kubernetes-development)
for the Podman prerequisites.

## Progress log

| Date | Item | Note |
| --- | --- | --- |
| 2026-07-20 | Program | Authored; build chain, package feed, and CI landed. Owner decision: no `IsAotCompatible` mandate in this repo. |
| 2026-09-09 – 2026-10-25 | First design | Kubernetes and Docker gateways on the first ApplicationModel design. Retired by the restart; last at `f779b85`. |
| 2026-10-27 | Application model restart | assimalign/cohesion#1759 replaced the first design. Contracts decision C5 defined the gateway/instance pairs. |
| 2026-10-30 | Kubernetes pair | Rebuilt on the restarted ApplicationModel; kind end-to-end passes locally. |
| 2026-10-31 | Docker pair | Rebuilt on the Kubernetes pair's pattern; Podman end-to-end passes locally. |
| 2026-10-31 | Revision rule | Kubernetes moved to Docker's revision and workload split (GK5, template version 3). |
