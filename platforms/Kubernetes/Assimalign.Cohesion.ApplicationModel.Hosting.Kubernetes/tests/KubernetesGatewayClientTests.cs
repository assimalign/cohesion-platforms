using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;
using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Tests;

public class KubernetesGatewayClientTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Kubernetes] - Gateway client: ";

    [Fact(DisplayName = DisplayPrefix + "A resource that opts in uses the Kubernetes client when its environment names the platform")]
    public void UseKubernetes_InKubernetesEnvironment_ShouldBeTheSelectedClient()
    {
        // Act
        TestOptions options = OptIn(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "kubernetes",
            [AppEnvironment.Variables.ManifestEndpoint] = "kubernetes:///namespaces/cohesion",
        });

        // Assert
        options.GetGatewayClient().ShouldNotBeNull().Platform.ShouldBe((PlatformName)"kubernetes");
    }

    [Theory(DisplayName = DisplayPrefix + "An environment that names no gateway endpoint or no pod fails the publication before it reaches the cluster")]
    [InlineData(null, "orders-api-1")]
    [InlineData("http://gateway:5000/", "orders-api-1")]
    [InlineData("kubernetes:///namespaces/cohesion", null)]
    public async Task PublishAsync_WithoutGatewayOrPod_ShouldThrow(string? endpoint, string? pod)
    {
        // Arrange
        var client = new KubernetesGatewayClient(new KubernetesInstanceEnvironment(endpoint, pod, "cohesion", null, KubernetesProtocol.DefaultControlPlanePort));
        IApplicationInstanceControlPlane controlPlane = new TestResource().CreateControlPlane();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => client.PublishAsync(controlPlane.Manifest).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "The control plane serves the gateway's reads, writes, and restores on its port, and stops with the client")]
    public async Task StartAsync_ThenGatewayCalls_ShouldServeTheControlPlane()
    {
        // Arrange
        int port = GetFreePort();
        var resource = new TestResource();
        IApplicationInstanceControlPlane controlPlane = resource.CreateControlPlane();
        var client = new KubernetesGatewayClient(new KubernetesInstanceEnvironment("kubernetes:///namespaces/cohesion", "orders-api-1", "cohesion", null, port));
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
        await client.StartAsync(controlPlane);

        try
        {
            // Act
            using HttpResponseMessage write = await http.PostAsync("actions/greeting", new StringContent("""{"text":"welcome"}""", Encoding.UTF8, "application/json"));
            using HttpResponseMessage read = await http.GetAsync("actions/greeting");
            using HttpResponseMessage restore = await http.PutAsync("state/greeting", new StringContent("""{"text":"restored"}""", Encoding.UTF8, "application/json"));
            using HttpResponseMessage rejected = await http.PostAsync("actions/greeting", new StringContent("""{"text":""}""", Encoding.UTF8, "application/json"));
            using HttpResponseMessage missing = await http.GetAsync("actions/log-level");
            using HttpResponseMessage manifest = await http.GetAsync("manifest");

            // Assert
            write.StatusCode.ShouldBe(HttpStatusCode.OK);
            using (JsonDocument result = JsonDocument.Parse(await write.Content.ReadAsStringAsync()))
            {
                result.RootElement.GetProperty("state").GetProperty("value").GetProperty("text").GetString().ShouldBe("welcome");
            }

            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            restore.StatusCode.ShouldBe(HttpStatusCode.OK);
            resource.Current.Text.ShouldBe("restored");
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            manifest.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await client.StopAsync();
        }

        await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("actions/greeting"));
    }

    [Fact(DisplayName = DisplayPrefix + "A client serves one control plane at a time")]
    public async Task StartAsync_Twice_ShouldThrow()
    {
        // Arrange
        var client = new KubernetesGatewayClient(new KubernetesInstanceEnvironment("kubernetes:///namespaces/cohesion", "orders-api-1", "cohesion", null, GetFreePort()));
        IApplicationInstanceControlPlane controlPlane = new TestResource().CreateControlPlane();
        await client.StartAsync(controlPlane);

        try
        {
            // Act & Assert
            await Should.ThrowAsync<InvalidOperationException>(() => client.StartAsync(controlPlane).AsTask());
        }
        finally
        {
            await client.StopAsync();
        }
    }

    private static TestOptions OptIn(IReadOnlyDictionary<string, string> variables)
    {
        using (AppEnvironment.Enter(AppEnvironmentScope.Create(variables, CancellationToken.None)))
        {
            return new TestOptions().UseKubernetes();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestOptions : ApplicationInstanceOptions
    {
    }
}
