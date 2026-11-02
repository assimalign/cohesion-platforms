# Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes — Overview

The gateway half of the Kubernetes platform. `KubernetesInfrastructure` runs a gateway's resources
on a cluster, receives the publications and renewals of their instances, and calls their control
planes, all through the cluster's API server. The gateway runs inside the cluster or outside it
alike.

```csharp
await using KubernetesInfrastructure kubernetes = KubernetesInfrastructure.Create(new KubernetesInfrastructureOptions
{
    Namespace = "shop",
});
kubernetes
    .AddImage("Web", "orders-api", "localhost:5001/orders-api:1.0")
    .AddImage("Web", "storefront", "localhost:5001/storefront:1.0");

ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop");
builder.UseKubernetes(kubernetes);
IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api").WithReplicas(2);
builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);

await builder.Build().RunAsync();
```

Each resource's program opts into the platform with the instance half,
`Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes`:

```csharp
builder.Services.AddResource("orders-api", resource => resource.UseKubernetes());
```

## Surface

| Member | Purpose |
| --- | --- |
| `KubernetesInfrastructure.Create(options)` | Connects to the cluster the options name: a kubeconfig path, `KUBECONFIG`, the default kubeconfig, or the pod's service account. |
| `KubernetesInfrastructure.AddImage(provider, name, image)` | Binds a resource to the container image that runs it. |
| `KubernetesInfrastructureOptions` | `KubeConfigPath`, `Context`, `Namespace` (`cohesion`), `EndpointPort` (8080), `ControlPlanePort` (9090), `ImagePullPolicy` (`IfNotPresent`). |
| `builder.UseKubernetes(infrastructure)` | Sets the infrastructure as the gateway's infrastructure provider, which is also its inbound channel, and its instance client. |
| `resource.WithReplicas(count)` | The resource's `replicas` extension on the `kubernetes` platform: how many pods its Deployment runs. |

What it runs per resource, how the two halves talk, and what is still to come back from the
original implementation are in [DESIGN.md](DESIGN.md).
