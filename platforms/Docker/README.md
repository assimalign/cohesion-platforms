# Docker (platform area)

The Docker implementation of Cohesion's application-model gateway: realize an `IApplicationModel`
as containers on a Docker-compatible engine — an application-scoped network, digest-verified image
loads, and the engine's event stream as the observed-state source. Podman's Docker-compatible
socket is the supported local target.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Docker` | `DockerGateway : ApplicationGateway` (`Name = "docker"`), engine client, container controller, event observer, `UseDockerGateway()`. (Scaffolded — implementation tracked by [#24](https://github.com/assimalign/cohesion-platforms/issues/24)–[#28](https://github.com/assimalign/cohesion-platforms/issues/28).) |

## Layering & posture

- Depends on: ApplicationModel contracts + Gateway base (NuGet) and `platforms/Containers`.
- The engine client is hand-rolled and AOT-safe (source-generated serialization); `Docker.DotNet`
  is ruled out by the repo AOT mandate.
- Shares ~80% of its machinery with the Kubernetes gateway via `platforms/Containers` by design.

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing.
