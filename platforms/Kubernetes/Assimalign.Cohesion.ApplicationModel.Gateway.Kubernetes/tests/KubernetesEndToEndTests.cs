using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// The Kubernetes platform end to end, on a kind cluster: the gateway runs here, outside the cluster,
/// and reaches it, as its instances reach it, only through the cluster's API server.
/// </summary>
/// <remarks>
/// Prerequisites: a cluster from <c>scripts/New-CohesionKindCluster.ps1</c>, and the fixture program's
/// image in its registry from <c>scripts/Publish-CohesionKindFixtures.ps1</c>. Set
/// <c>COHESION_KIND_CONTEXT</c> to the cluster's context to run it.
/// </remarks>
public class KubernetesEndToEndTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Kubernetes] - End to end on kind: ";

    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(4);

    [KindFact(DisplayName = DisplayPrefix + "The gateway deploys its resources, their pods publish, the storefront reaches orders-api at its endpoint, and a write reaches every instance")]
    public async Task StartAsync_OnKind_ShouldDeployManageAndServeTheResources()
    {
        // Arrange: one image runs both resources. orders-api runs two instances, and the storefront's
        // setting is orders-api's endpoint, which the platform's Service gives it.
        await using KindCluster cluster = KindCluster.Connect();
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.StartupTimeout = _startupTimeout;
        builder.UseKubernetes(kubernetes => cluster.Configure(kubernetes)
            .AddImage("Web", "orders-api", cluster.Image)
            .AddImage("Web", "storefront", cluster.Image));
        IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api").WithReplicas(2);
        builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);
        JsonApplicationContentSerializer json = JsonApplicationContentSerializer.Create(TestJsonContext.Default);

        // Act: the storefront answers through its Service once its server listens, which follows its
        // publication; the second orders-api pod may publish after the gateway started.
        await using GatewayRun run = await GatewayRun.StartAsync(builder.Build(), _startupTimeout + TimeSpan.FromMinutes(1));
        string answer = await Eventually.GetAsync(() => GetAsync(cluster, "storefront"), IsUnavailable, TimeSpan.FromMinutes(1));
        IApplicationGatewayDeployment ordersApi = run.Gateway.Resources.Single(static resource => resource.Name.Value == "orders-api");
        await Eventually.WaitUntilAsync(() => ordersApi.Instances.Count == 2, TimeSpan.FromMinutes(2));
        ApplicationActionResult write = await run.Gateway.ExecuteAsync(
            "Web",
            "orders-api",
            "greeting",
            ApplicationActionContext.Create(ApplicationActionOperation.Write, json, json.Serialize(new Greeting("hello kubernetes"))));

        // Assert: the write ran on one instance and reached the other, by the gateway's restore or, when
        // the restore came before that instance's control plane opened, by its next publication, after
        // which both are registered again.
        answer.ShouldBe("storefront -> orders-api");
        ordersApi.Outputs[OutputName.Endpoint].ShouldBe($"http://orders-api.{cluster.Namespace}.svc.cluster.local");
        write.Status.ShouldBe(ApplicationActionStatus.Succeeded);

        // Every pod becomes ready, as its readiness probe decides: its program accepts connections on its
        // http endpoint, which it opens only after its instance published.
        await Eventually.WaitUntilAsync(() => ArePodsReadyAsync(cluster, expected: 3), TimeSpan.FromMinutes(1));
        // Each instance holds the write: the test reads it from every one with a client of its own, as
        // the gateway owns its infrastructure.
        await Eventually.WaitUntilAsync(() => ordersApi.Instances.Count == 2, TimeSpan.FromMinutes(1));
        await using KubernetesInfrastructure reader = cluster.CreateInfrastructure();
        IApplicationGatewayInstanceClient client = reader;
        ApplicationInstanceManifest[] instances = [.. ordersApi.Instances];
        instances.Length.ShouldBe(2);
        foreach (ApplicationInstanceManifest instance in instances)
        {
            ApplicationActionResult read = await Eventually.GetAsync(
                () => client.ExecuteAsync(instance, "greeting", ApplicationActionContext.Create(ApplicationActionOperation.Read, json)).AsTask(),
                static exception => exception is HttpRequestException,
                TimeSpan.FromSeconds(30));
            read.Status.ShouldBe(ApplicationActionStatus.Succeeded);
            json.Deserialize<Greeting>(read.Output).ShouldNotBeNull().Text.ShouldBe("hello kubernetes");
        }
    }

    // A GET of the resource's Service through the API server's service proxy, as a client in the
    // cluster would reach it.
    private static async Task<string> GetAsync(KindCluster cluster, string resource)
    {
        using Stream body = await cluster.Client.CoreV1.ConnectGetNamespacedServiceProxyAsync(string.Concat(resource, ":http"), cluster.Namespace);
        using var reader = new StreamReader(body);
        return await reader.ReadToEndAsync();
    }

    // The namespace runs the expected pods, each with a readiness probe and each ready.
    private static async Task<bool> ArePodsReadyAsync(KindCluster cluster, int expected)
    {
        V1PodList pods = await cluster.Client.CoreV1.ListNamespacedPodAsync(cluster.Namespace);
        return pods.Items.Count == expected && pods.Items.All(static pod =>
            pod.Spec.Containers.All(static container => container.ReadinessProbe is not null)
            && pod.Status?.Conditions?.Any(static condition => condition.Type == "Ready" && condition.Status == "True") == true);
    }

    // Until a pod is ready, the proxy answers that the Service has no endpoint, or that the pod refused.
    private static bool IsUnavailable(Exception exception)
    {
        return exception is HttpRequestException
            || exception is HttpOperationException { Response.StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.InternalServerError };
    }
}
