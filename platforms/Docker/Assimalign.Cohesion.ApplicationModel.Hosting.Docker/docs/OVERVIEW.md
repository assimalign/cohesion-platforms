# Assimalign.Cohesion.ApplicationModel.Hosting.Docker — Overview

The instance half of the Docker platform: the gateway client a resource's program uses when its
gateway runs it as a container on a Docker-compatible engine. The program opts in on its resource's
options, as it opts into every platform it may run on:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("orders-api", resource => resource.UseDocker());
```

`UseDocker()` is a generic extension of every area's `ApplicationInstanceOptions`, so it keeps the
area's options type for chaining. The client is selected only when the instance's environment names
the platform (`COHESION_GATEWAY=docker`), which the Docker gateway sets on every container it runs;
without a platform the program runs unmanaged.

Once selected, the instance serves its side of the publication channel and its control plane on its
container's control-plane port. The engine publishes that port on a loopback port of its host, where
the gateway polls the instance, answers its publication, sees its renewals, and calls its actions.
The program needs nothing else: no address of its gateway, and no port of its own to publish.

The gateway half is `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`; how the two halves talk is
in its [DESIGN.md](../../Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/DESIGN.md), and the
instance's side of it is in [DESIGN.md](DESIGN.md).
