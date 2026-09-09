# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Overview

The Docker platform gateway: `DockerGateway : ApplicationGateway` (`Name = "docker"`) compiles
validated `ResourcePlan` values into containers on a Docker-compatible engine (Podman locally).

**Scope (per the [program plan](../../../../docs/PLATFORMS_PROGRAM_PLAN.md)):**

- Minimal typed Docker Engine API client (npipe/unix socket, source-generated
  serialization). ([#24](https://github.com/assimalign/cohesion-platforms/issues/24))
- `DockerPlanCompiler` + controller: app-scoped network, container/run-once task, named volumes,
  tmpfs inputs, ports, probes, and ownership labels from the generic plan plus explicit compiler
  inputs. The single compiler never dispatches on kind, area, or CLR type.
- Gateway + digest-verified image load from OCI tarballs and `UseDockerGateway()`.
- Observer: events stream + inspect reconciliation → lifecycle and observed endpoints, including
  non-gating `Degraded` after readiness.
- Podman E2E harness. ([#28](https://github.com/assimalign/cohesion-platforms/issues/28))

**Dependencies:** generic ApplicationModel + Gateway base (NuGet), `platforms/Containers`.
COHPLT001 rejects resource-area `.ApplicationModel`, `*.Hosting`, `.Application` runtime, and
`Microsoft.Extensions.*` assemblies from the resolved closure.

**Status:** scaffolded; no implementation yet.
