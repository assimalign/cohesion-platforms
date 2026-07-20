# Containers (platform area)

Shared, platform-neutral container-gateway infrastructure used by both the Docker and Kubernetes
gateways. This area exists so the two platform gateways stay thin: everything about *images* —
locating them, verifying them, indexing them, and (eventually) serving them — lives here.

## Projects

| Project | Purpose |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel.Gateway.Containers` | Container artifact + `application.images.json` image-index model, public gateway state manager, OCI image store, shared test primitives. (Scaffolded — implementation tracked by [#8](https://github.com/assimalign/cohesion-platforms/issues/8), [#9](https://github.com/assimalign/cohesion-platforms/issues/9), [#11](https://github.com/assimalign/cohesion-platforms/issues/11).) |

Planned siblings per the [program plan](../../docs/PLATFORMS_PROGRAM_PLAN.md): the image-gathering
build seam ([#12](https://github.com/assimalign/cohesion-platforms/issues/12)), the sample E2E
resource ([#10](https://github.com/assimalign/cohesion-platforms/issues/10)), and the embedded OCI
Distribution registry ([#13](https://github.com/assimalign/cohesion-platforms/issues/13)).

## Layering

- This repo realizes Cohesion's L2 application model onto deployment targets; Containers is the
  shared substrate of that realization.
- Depends on `Assimalign.Cohesion.ApplicationModel` + `.Gateway` (NuGet, from the cohesion repo).
- Platform gateways (`platforms/Kubernetes`, `platforms/Docker`) depend on this area — never the
  reverse, and the two platform areas never reference each other.

See `.claude/rules/platform-areas.md` for the binding architecture rules.
