# Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes — Overview

The instance half of the Kubernetes platform: the gateway client a resource's program uses when its
gateway runs it on Kubernetes. The program opts in on its resource's options, as it opts into every
platform it may run on:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("orders-api", resource => resource.UseKubernetes());
```

`UseKubernetes()` is a generic extension of every area's `ApplicationInstanceOptions`, so it keeps
the area's options type for chaining. The client is selected only when the instance's environment
names the platform (`COHESION_GATEWAY=kubernetes`), which the Kubernetes gateway sets on every pod it
runs; without a platform the program runs unmanaged.

Once selected, the instance publishes and renews through a Lease of its own in the cluster's API
server and serves its control plane on its pod's control-plane port, which the gateway calls through
the API server's pod proxy. The program needs nothing else: no address of its gateway, no port of
its own to publish, and no permission beyond the Leases of its namespace, which the gateway grants
its service account.

The gateway half is `Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`; how the two halves
talk is in its [DESIGN.md](../../Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes/docs/DESIGN.md),
and the instance's side of it is in [DESIGN.md](DESIGN.md).
