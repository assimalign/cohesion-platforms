# Docker (platform area)

The Docker implementation of Cohesion's application-model gateway. `DockerPlanCompiler` is the
platform's one pure compiler from validated `ResourcePlan` values to Docker operations;
`DockerPlanController` applies those operations idempotently to a Docker-compatible engine. The
engine's event stream supplies observed state. Podman's Docker-compatible socket is the supported
local target.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Docker` | `DockerGateway : ApplicationGateway` (`Name = "docker"`), `DockerPlanCompiler`, plan controller, engine client, event observer, `UseDockerGateway()`. The compiler/controller work follows design item 33's re-pin. |

## Layering & posture

- Depends on the generic ApplicationModel contract + Gateway base (NuGet) and
  `platforms/Containers`; it never references a resource area's `.ApplicationModel`, `*.Hosting`,
  `.Application` runtime, or `Microsoft.Extensions.*` assembly.
- The engine client is hand-rolled with source-generated serialization; `Docker.DotNet` remains
  disfavored on dependency-hygiene grounds (large surface, external serializer dependency) — no
  longer an AOT question, since this repo carries no AOT mandate.
- Shares ~80% of its machinery with the Kubernetes gateway via `platforms/Containers` by design.
- Uses cohesion's public `InMemoryResourceStateManager`. Container health and plan probes produce
  `Running`, `Failed`, or `Stopped` gate outcomes; `Degraded` is observed after readiness and never
  re-gates an admitted dependent.

See `.claude/rules/platform-areas.md` for the binding architecture rules and
[docs/PLATFORMS_PROGRAM_PLAN.md](../../docs/PLATFORMS_PROGRAM_PLAN.md) for sequencing.
