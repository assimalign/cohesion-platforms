# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Overview

The gateway half of the Docker platform. `DockerInfrastructure` runs a gateway's resources as
containers on a Docker-compatible engine, Podman's included, receives the publications and renewals
of their instances by polling their containers' published control-plane ports, and calls their
control planes there. The gateway runs on the engine's host.

```csharp
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseDocker(docker =>
{
    docker.Options.Network = "shop";
    docker
        .AddImage("Web", "orders-api", "orders-api:1.0")
        .AddImage("Web", "storefront", "storefront:1.0");
});
IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api").WithDockerReplicas(2);
builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);

await using ApplicationGateway gateway = builder.Build();
await gateway.RunAsync();
```

The gateway's `Build()` runs the callback and creates the infrastructure, and the gateway owns it:
disposing the gateway disposes it. An infrastructure the program creates itself, with
`DockerInfrastructure.Create(options)` or a `DockerInfrastructureBuilder`, goes to
`builder.UseDocker(infrastructure)`; the gateway borrows it, and the program disposes it after the
gateway stops.

Each resource's program opts into the platform with the instance half,
`Assimalign.Cohesion.ApplicationModel.Hosting.Docker`:

```csharp
builder.Services.AddResource("orders-api", resource => resource.UseDocker());
```

## Surface

| Member | Purpose |
| --- | --- |
| `builder.UseDocker(configure)` | Builds the infrastructure from a `DockerInfrastructureBuilder` when the gateway is built, and sets it as the gateway's infrastructure provider, which is also its inbound channel, and its instance client. The gateway owns it. |
| `builder.UseDocker(infrastructure)` | Sets an infrastructure the caller created in both roles. The gateway borrows it. |
| `DockerInfrastructureBuilder` | `Options`, `AddImage(provider, name, image)`, and `Build()`, which creates the infrastructure with a copy of the options and every image bound. |
| `DockerInfrastructure.Create(options)` | Resolves the engine the options name: `Endpoint`, `DOCKER_HOST`, then the platform's default socket or pipe. |
| `DockerInfrastructure.AddImage(provider, name, image)` | Binds a resource to the container image that runs it. |
| `DockerInfrastructureOptions` | `Endpoint`, `Network` (`cohesion`), `EndpointPort` (8080), `ControlPlanePort` (9090), `ImagePullPolicy` (`IfNotPresent`). |
| `resource.WithDockerReplicas(count)` | The resource's `replicas` extension on the `docker` platform: how many containers run it. |

A resource's `endpoint` output is `http://{name}:8080`, its name on the network. The engine also
publishes each container's `http` port on a loopback port of its host, which `docker ps` shows.

What runs per resource, how the two halves talk, and what is still open are in
[DESIGN.md](DESIGN.md).
