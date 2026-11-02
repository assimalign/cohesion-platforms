# Deployment Platforms Program Plan (`L04.01`)

What this repository's platforms do today, what comes next, and what they depend on in the cohesion
repository. Decisions live in each pair's `docs/DESIGN.md`; this document tracks scope and sequencing.

## Where the program stands

Cohesion restarted its application model on 2026-10-27 (assimalign/cohesion#1759). The restart
replaced this repository's first design wholesale, and each platform was rebuilt on it as a **platform
pair** (contracts decision C5): a gateway half that implements the gateway's infrastructure provider
and instance client, and an instance half that a resource's program opts into. The rules are in
`.claude/rules/platform-areas.md`; the first design is in this repository's history, last at
`f779b85`.

| Platform | Pair | Status |
| --- | --- | --- |
| Kubernetes | `Gateway.Kubernetes` + `Hosting.Kubernetes` | Deployments and Services in one namespace, Lease channel, pod-proxy calls, readiness probe, replicas. End-to-end on kind passes locally (first 2026-10-30). |
| Docker | `Gateway.Docker` + `Hosting.Docker` | One container per replica on one network, polled channel on published loopback ports, adoption, fail-fast watch, replicas. End-to-end on rootful Podman passes locally (first 2026-10-31). |

Both platforms follow the provider contract's revision rule: the plan's revision covers only what the
model does not carry, and a separate workload hash decides create, update, or nothing (Kubernetes
GK5, Docker GD5).

## What comes next

1. **Backfill Kubernetes** from the first implementation, by the list under "Backfill" in
   `Gateway.Kubernetes/docs/DESIGN.md`: other workload kinds, volumes and claims, liveness and
   startup probes, ConfigMap and Secret inputs, autoscaling and disruption budgets, the system
   installation and its exposure, the gateway store, and image publication.
2. **Close the open questions** each gateway half's `DESIGN.md` lists. The ones that block leaving a
   test environment are the same on both platforms: the instance control plane and channel have no
   authentication of their own (Kubernetes 2, Docker 1), and readiness without an `http` endpoint
   (Kubernetes 1, Docker 4).
3. **Run the end-to-end workflows on GitHub.** `e2e-kubernetes.yml` and `e2e-docker.yml` have not yet
   run there; once a dispatched run of each passes, it can gate pushes to its platform.
4. **Further platforms** follow "Adding a platform" in `.claude/rules/platform-areas.md`.

## Work items

Program root: **[#1](https://github.com/assimalign/cohesion-platforms/issues/1) `[L04.01.00] Cohesion - Deployment Platforms`**
in the shared org Project #13, `Codebase = cohesion-platforms`, issues on
`assimalign/cohesion-platforms`. Area epics: Delivery (#2), Kubernetes (#14), Docker (#23). The
features filed under them before 2026-10-27 describe the first design; new work is filed against the
platform pairs with the `cohesion-work-items` skill.

## Cross-repository dependencies

1. **The cohesion pin.** Every Cohesion package is consumed at `CohesionPackageVersion` in
   `build/Targets/Build.References.Packages.targets`, today a pull-request CI build of
   assimalign/cohesion#1784 (stacked on #1759). Its versions are deleted when that pull request
   closes: move the pin to a cohesion `main` CI build once it merges. The pin, `pr.1784.179`, was
   built from cohesion 9c402d1bc and carries `ApplicationGatewayBuilder.UsePlatform`, which
   `UseKubernetes` and `UseDocker` build on, and the gateway fix that counts an instance publishing
   before its resource is planned.
2. **The contracts.** A gateway half follows cohesion's `ApplicationModel.Gateway` design and an
   instance half its `ApplicationModel.Hosting` design; a contract change there is a change to both
   pairs here. `docs/RUNTIME_CONTRACT.md` in cohesion is the runtime contract every platform hands its
   instances. **Order the pushes:** a cohesion contract change is pushed and CI-packed (its
   `ci-packages` run on #1759, or on `main`) before the platforms change that consumes it, and the pin
   moves to that run's version in the same change. Verify without the sibling override first: restore
   from GitHub Packages in a copy outside the tree, then build, since a build against
   `CohesionSiblingPackageVersion` proves nothing about CI.
3. **Downstream consumers (planned).** `cohesion-examples` will consume this repository's CI packages,
   through a Kubernetes example (`examples/libraries/ApplicationModel/web-on-kubernetes`), once
   `ci-packages.yml` has published a first build to pin; until then no consumer exists. A
   public-surface change then lands there in the same window.

## Environment (local dev)

- Windows ARM64 with a rootful Podman 5.8 machine; kind 0.33 for the Kubernetes end-to-end
  (`platforms/Kubernetes/README.md`), and the machine's Docker-compatible pipe for Docker
  (`platforms/Docker/README.md`). Fixture images are built for the machine's architecture.

## Progress log

| Date | Item | Note |
| --- | --- | --- |
| 2026-07-20 | Program | Authored; build chain, package feed, and CI landed. Owner decision: no `IsAotCompatible` mandate in this repo. |
| 2026-09-09 – 2026-10-25 | First design | Kubernetes and Docker gateways on the first ApplicationModel design. Retired by the restart; last at `f779b85`. |
| 2026-10-30 | Kubernetes pair | Rebuilt on the restarted ApplicationModel; kind end-to-end passes locally. |
| 2026-10-31 | Docker pair | Rebuilt on the Kubernetes pair's pattern; Podman end-to-end passes locally. |
| 2026-10-31 | Revision rule | Kubernetes moved to Docker's revision and workload split (GK5, template version 3). |
