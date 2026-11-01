# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Overview

The gateway half of the Kubernetes platform. `KubernetesInfrastructure` runs a gateway's resources
on a cluster, receives the publications and renewals of their instances, and calls their control
planes, all through the cluster's API server. The gateway runs inside the cluster or outside it
alike.

```csharp
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseKubernetes(kubernetes =>
{
    kubernetes.Options.Namespace = "shop";
    kubernetes.Options.ImagePullPolicy = "Always";
    kubernetes
        .AddImage("Web", "orders-api", "localhost:5001/orders-api:1.0")
        .AddImage("Web", "storefront", "localhost:5001/storefront:1.0");
});
IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api").WithReplicas(2);
builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);

await using ApplicationGateway gateway = builder.Build();
await gateway.RunAsync();
```

The gateway's `Build()` runs the callback and creates the infrastructure, and the gateway owns it:
disposing the gateway disposes it. An infrastructure the program creates itself, with
`KubernetesInfrastructure.Create(options)` or a `KubernetesInfrastructureBuilder`, goes to
`builder.UseKubernetes(infrastructure)`; the gateway borrows it, and the program disposes it after
the gateway stops.

Each resource's program opts into the platform with the instance half,
`Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes`:

```csharp
builder.Services.AddResource("orders-api", resource => resource.UseKubernetes());
```

## Surface

| Member | Purpose |
| --- | --- |
| `builder.UseKubernetes(configure)` | Builds the infrastructure from a `KubernetesInfrastructureBuilder` when the gateway is built, and sets it as the gateway's infrastructure provider, which is also its inbound channel, and its instance client. The gateway owns it. |
| `builder.UseKubernetes(infrastructure)` | Sets an infrastructure the caller created in both roles. The gateway borrows it. |
| `KubernetesInfrastructureBuilder` | `Options`, `AddImage(provider, name, image)`, and `Build()`, which creates the infrastructure with a copy of the options and every image bound. |
| `KubernetesInfrastructure.Create(options)` | Connects to the cluster the options name: a kubeconfig path, `KUBECONFIG`, the default kubeconfig, or the pod's service account. |
| `KubernetesInfrastructure.AddImage(provider, name, image)` | Binds a resource to the container image that runs it. |
| `KubernetesInfrastructureOptions` | `KubeConfigPath`, `Context`, `Namespace` (`cohesion`), `EndpointPort` (8080), `ControlPlanePort` (9090), `ImagePullPolicy` (`IfNotPresent`). |
| `resource.WithReplicas(count)` | The resource's `replicas` extension on the `kubernetes` platform: how many pods its Deployment runs. |

What it runs per resource, how the two halves talk, and what is still to come back from the
original implementation are in [DESIGN.md](DESIGN.md).
