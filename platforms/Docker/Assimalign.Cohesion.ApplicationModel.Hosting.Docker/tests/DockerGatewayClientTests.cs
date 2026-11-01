using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Docker;
using Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Tests;

public class DockerGatewayClientTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Docker] - Gateway client: ";

    private static readonly JsonApplicationContentSerializer _protocol = JsonApplicationContentSerializer.Create();

    [Fact(DisplayName = DisplayPrefix + "A resource that opts in uses the Docker client when its environment names the platform")]
    public void UseDocker_InDockerEnvironment_ShouldBeTheSelectedClient()
    {
        // Act
        TestOptions options;
        using (AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "docker",
            [AppEnvironment.Variables.ManifestEndpoint] = "docker:///networks/cohesion",
        }, CancellationToken.None)))
        {
            options = new TestOptions().UseDocker();
        }

        // Assert
        options.GetGatewayClient().ShouldNotBeNull().Platform.ShouldBe((PlatformName)"docker");
    }

    [Theory(DisplayName = DisplayPrefix + "An environment that names no gateway endpoint or no container fails the publication before it opens its port")]
    [InlineData(null, "orders-api-0")]
    [InlineData("kubernetes:///namespaces/cohesion", "orders-api-0")]
    [InlineData("docker:///networks/cohesion", null)]
    public async Task PublishAsync_WithoutGatewayOrContainer_ShouldThrow(string? endpoint, string? container)
    {
        // Arrange
        var client = new DockerGatewayClient(new DockerInstanceEnvironment(endpoint, container, GetFreePort()));
        IApplicationInstanceControlPlane controlPlane = new TestResource().CreateControlPlane();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => client.PublishAsync(controlPlane.Manifest).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "Publishing serves the manifest on the channel and returns the gateway's answer")]
    public async Task PublishAsync_ThenGatewayAnswers_ShouldReturnThePublication()
    {
        // Arrange
        await using var instance = new Instance();

        // Act: the gateway reads the channel, then answers the key it names.
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        DockerChannelState state = await instance.ReadStateAsync();
        ApplicationInstanceManifest manifest = _protocol.Deserialize<ApplicationInstanceManifest>(Encoding.UTF8.GetBytes(state.Manifest)).ShouldNotBeNull();
        HttpStatusCode stale = await instance.AnswerAsync(new DockerChannelAnswer(InstanceKey.New().ToString(), Publication(TimeSpan.FromSeconds(30)), null));
        HttpStatusCode answered = await instance.AnswerAsync(new DockerChannelAnswer(state.InstanceKey, Publication(TimeSpan.FromSeconds(30)), null));
        ApplicationInstancePublication publication = await publishing.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        state.InstanceKey.ShouldBe(instance.ControlPlane.Manifest.Key.ToString());
        state.PublicationKey.ShouldBeNull();
        manifest.ControlPlaneEndpoint.ShouldBe(new Uri($"docker:///networks/cohesion/containers/orders-api-0:{instance.Port}/"));
        stale.ShouldBe(HttpStatusCode.Conflict);
        answered.ShouldBe(HttpStatusCode.NoContent);
        publication.Lease.ShouldBe(TimeSpan.FromSeconds(30));
        (await instance.ReadStateAsync()).PublicationKey.ShouldBe(state.InstanceKey);
    }

    [Fact(DisplayName = DisplayPrefix + "A refusal fails the publication with the gateway's reason")]
    public async Task PublishAsync_ThenGatewayRefuses_ShouldThrow()
    {
        // Arrange
        await using var instance = new Instance();

        // Act
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        DockerChannelState state = await instance.ReadStateAsync();
        await instance.AnswerAsync(new DockerChannelAnswer(state.InstanceKey, null, "no such resource"));

        // Assert
        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => publishing.WaitAsync(TimeSpan.FromSeconds(10)));
        refusal.Message.ShouldContain("no such resource", Case.Sensitive);
        (await instance.ReadStateAsync()).RefusalKey.ShouldBe(state.InstanceKey);
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal moves the count while the gateway knows the instance, and reports it unknown once the gateway clears its answer")]
    public async Task RenewAsync_AfterTheGatewayClears_ShouldReportUnknown()
    {
        // Arrange
        await using var instance = new Instance();
        await instance.PublishAsync();
        ApplicationInstanceManifest manifest = instance.ControlPlane.Manifest;

        // Act
        bool renewed = await instance.Client.RenewAsync(manifest);
        long renewal = (await instance.ReadStateAsync()).Renewal;
        using HttpResponseMessage cleared = await instance.Http.DeleteAsync($"channel/answer/{manifest.Key}");
        bool unknown = await instance.Client.RenewAsync(manifest);

        // Assert
        renewed.ShouldBeTrue();
        renewal.ShouldBe(1);
        cleared.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        unknown.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "The control plane answers no call before it starts, then serves the gateway's reads, writes, and restores on the same port, and stops with the client")]
    public async Task StartAsync_ThenGatewayCalls_ShouldServeTheControlPlane()
    {
        // Arrange
        await using var instance = new Instance();
        await instance.PublishAsync();
        using HttpResponseMessage early = await instance.Http.GetAsync("actions/greeting");
        await instance.Client.StartAsync(instance.ControlPlane);

        // Act
        using HttpResponseMessage write = await instance.Http.PostAsync("actions/greeting", new StringContent("""{"text":"welcome"}""", Encoding.UTF8, "application/json"));
        using HttpResponseMessage read = await instance.Http.GetAsync("actions/greeting");
        using HttpResponseMessage restore = await instance.Http.PutAsync("state/greeting", new StringContent("""{"text":"restored"}""", Encoding.UTF8, "application/json"));
        using HttpResponseMessage rejected = await instance.Http.PostAsync("actions/greeting", new StringContent("""{"text":""}""", Encoding.UTF8, "application/json"));
        using HttpResponseMessage missing = await instance.Http.GetAsync("actions/log-level");

        // Assert
        early.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        write.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        restore.StatusCode.ShouldBe(HttpStatusCode.OK);
        instance.Resource.Current.Text.ShouldBe("restored");
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await instance.Client.StopAsync();
        await Should.ThrowAsync<HttpRequestException>(() => instance.Http.GetAsync("actions/greeting"));
    }

    [Fact(DisplayName = DisplayPrefix + "A client serves one control plane at a time")]
    public async Task StartAsync_Twice_ShouldThrow()
    {
        // Arrange
        await using var instance = new Instance();
        await instance.PublishAsync();
        await instance.Client.StartAsync(instance.ControlPlane);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => instance.Client.StartAsync(instance.ControlPlane).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "Stopping the client ends a publication still waiting for its answer at once")]
    public async Task StopAsync_WhilePublishing_ShouldCancelThePublication()
    {
        // Arrange
        await using var instance = new Instance();
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        await instance.ReadStateAsync();

        // Act
        await instance.Client.StopAsync();

        // Assert: well inside the 60-second answer window.
        await Should.ThrowAsync<OperationCanceledException>(() => publishing.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = DisplayPrefix + "A body longer than the port reads is refused with 413, and routes that take none answer as before")]
    public async Task Listener_WithOversizedBody_ShouldRefuseIt()
    {
        // Arrange
        await using var instance = new Instance();
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        await instance.ReadStateAsync();
        byte[] oversized = new byte[InstanceControlPlaneListenerLimit + 1];

        // Act
        using HttpResponseMessage answer = await instance.Http.PostAsync("channel/answer", new ByteArrayContent(oversized));
        using HttpResponseMessage unknown = await instance.Http.PostAsync("nowhere", new ByteArrayContent([1, 2, 3]));
        using HttpResponseMessage state = await instance.Http.PostAsync("channel", new ByteArrayContent([1, 2, 3]));

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        unknown.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        state.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        publishing.IsCompleted.ShouldBeFalse();
    }

    private const int InstanceControlPlaneListenerLimit = 4 * 1024 * 1024;

    private static string Publication(TimeSpan lease)
    {
        return Encoding.UTF8.GetString(_protocol.Serialize(new ApplicationInstancePublication([], lease)).Span);
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

    // One instance's client on a port of its own, and the gateway's side of its channel.
    private sealed class Instance : IAsyncDisposable
    {
        public Instance()
        {
            Port = GetFreePort();
            Resource = new TestResource();
            ControlPlane = Resource.CreateControlPlane();
            Client = new DockerGatewayClient(new DockerInstanceEnvironment("docker:///networks/cohesion", "orders-api-0", Port));
            Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}/") };
        }

        public int Port { get; }

        public TestResource Resource { get; }

        public IApplicationInstanceControlPlane ControlPlane { get; }

        public DockerGatewayClient Client { get; }

        public HttpClient Http { get; }

        // Publishes and answers as a gateway would, until the publication returns.
        public async Task PublishAsync()
        {
            Task<ApplicationInstancePublication> publishing = Client.PublishAsync(ControlPlane.Manifest).AsTask();
            DockerChannelState state = await ReadStateAsync();
            await AnswerAsync(new DockerChannelAnswer(state.InstanceKey, Publication(TimeSpan.FromSeconds(30)), null));
            await publishing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The listener opens as the publication starts; the gateway polls until it answers.
        public async Task<DockerChannelState> ReadStateAsync()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return (await Http.GetFromJsonAsync("channel", DockerProtocolJsonContext.Default.DockerChannelState)).ShouldNotBeNull();
                }
                catch (HttpRequestException) when (attempt < 50)
                {
                    await Task.Delay(100);
                }
            }
        }

        public async Task<HttpStatusCode> AnswerAsync(DockerChannelAnswer answer)
        {
            using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(answer, DockerProtocolJsonContext.Default.DockerChannelAnswer));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await Http.PostAsync("channel/answer", content);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            Http.Dispose();
        }
    }
}
