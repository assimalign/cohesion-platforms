# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Overview

The Docker platform gateway: `DockerGateway : ApplicationGateway` (`Name = "docker"`) realizes an
`IApplicationModel` as containers on a Docker-compatible engine (Podman locally).

**Scope (per the [program plan](../../../../docs/PLATFORMS_PROGRAM_PLAN.md)):**

- Minimal typed Docker Engine API client (npipe/unix socket, source-generated
  serialization). ([#24](https://github.com/assimalign/cohesion-platforms/issues/24))
- Gateway + digest-verified image load from OCI tarballs, `UseDockerGateway()`. ([#25](https://github.com/assimalign/cohesion-platforms/issues/25))
- Container controller: app-scoped network, env/mounts/ports from capability interfaces,
  ownership labels. ([#26](https://github.com/assimalign/cohesion-platforms/issues/26))
- Observer: events stream + inspect reconciliation → lifecycle + observed endpoints. ([#27](https://github.com/assimalign/cohesion-platforms/issues/27))
- Podman E2E harness. ([#28](https://github.com/assimalign/cohesion-platforms/issues/28))

**Dependencies:** ApplicationModel + Gateway base (NuGet), `platforms/Containers`.

**Status:** scaffolded; no implementation yet.
