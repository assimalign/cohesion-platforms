# Assimalign.Cohesion.ApplicationModel.Gateway.Docker

Public API for selecting, configuring, and directly rendering the Docker implementation of
Cohesion's application-model gateway. The assembly targets Docker-compatible Engine APIs and uses
the generic `ResourcePlan` contract; it does not reference a resource area's application-model or
hosting assembly.

## Public types

| Type | Purpose |
| --- | --- |
| [`DockerGateway`](DockerGateway/OVERVIEW.md) | Concrete `ApplicationGateway` for Docker-compatible engines and the implementation of the direct Compose-style renderer. |
| [`DockerGatewayOptions`](DockerGatewayOptions/OVERVIEW.md) | Engine, image, observation, probe, warning, and restart-supervision settings. |
| [`IDockerComposeRenderer`](IDockerComposeRenderer/OVERVIEW.md) | Daemon-free, deterministic renderer for one resolved resource plan. |
| [`DockerGatewayExtensions`](DockerGatewayExtensions/OVERVIEW.md) | `IApplicationBuilder.UseDockerGateway(...)` selection and command-line overloads. |

## Usage

```csharp
IApplicationBuilder builder = Application.CreateBuilder(applicationName, args);

builder.UseDockerGateway(options =>
{
    options.EngineEndpoint = new Uri("unix:///var/run/docker.sock");
    options.ImageIndexPath = "application.images.json";
    options.ContainerRegistry = "registry.example.test:5000";
    options.PublicHost = "localhost";
});
```

The gateway's normal reconcile path contacts the selected engine. In contrast,
`IDockerComposeRenderer.Render(...)` only compiles its supplied plan, artifact, inputs, and
dependency snapshot and does not create an engine client.

## Current integration boundaries

- The canonical `Assimalign.Cohesion.ApplicationModel` `10.0.1-preview.3` DLL resolved by this
  project does not export the sibling source's `IApplicationGatewayRenderer` contract. The direct
  Docker renderer works, but application-set `--mode render --gateway docker` cannot discover it
  until that contract is published in a new immutable upstream package and the package floor is
  advanced.
- Sensitive paths are represented as tmpfs plus archive entries, but the current post-start Moby
  archive path cannot reliably populate the live tmpfs or bootstrap PID 1 atomically. See the
  project [design document](../../DESIGN.md) for the exact engine boundary.

See the project [overview](../../OVERVIEW.md) for lifecycle and dependency context.
