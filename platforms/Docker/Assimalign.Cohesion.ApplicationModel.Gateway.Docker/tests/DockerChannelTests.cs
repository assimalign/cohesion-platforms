using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.ApplicationModel.Hosting.Docker;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// The channel against the instance half in this process: the fake engine lists a container whose
/// control-plane port is published where the instance's client listens, so the gateway's poll reaches
/// the instance as it would through the engine.
/// </summary>
public class DockerChannelTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - Channel: ";

    private static readonly TimeSpan _lease = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = DisplayPrefix + "A poll finds an unanswered publication, hands it to the gateway, and answers the instance")]
    public async Task PollAsync_WithPublication_ShouldAnswerTheInstance()
    {
        // Arrange
        await using var instance = new Instance();
        var gateway = new RecordingPublisher();

        // Act
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        await instance.PollUntilAsync(gateway, () => publishing.IsCompleted);
        ApplicationInstancePublication publication = await publishing;

        // Assert
        ApplicationInstanceManifest published = gateway.Published.ShouldHaveSingleItem();
        published.Key.ShouldBe(instance.ControlPlane.Manifest.Key);
        published.ControlPlaneEndpoint.ShouldBe(new Uri($"docker:///networks/cohesion/containers/{Instance.Container}:{instance.Port}/"));
        publication.Lease.ShouldBe(_lease);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication the gateway refuses fails the instance's publication with the gateway's reason")]
    public async Task PollAsync_WithRefusedPublication_ShouldRefuseTheInstance()
    {
        // Arrange
        await using var instance = new Instance();
        var gateway = new RecordingPublisher { Refusal = "resource 'Web/orders-api' is not in the model" };

        // Act
        Task<ApplicationInstancePublication> publishing = instance.Client.PublishAsync(instance.ControlPlane.Manifest).AsTask();
        await instance.PollUntilAsync(gateway, () => publishing.IsCompleted);

        // Assert
        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => publishing);
        refusal.Message.ShouldContain("not in the model", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal is handed to the gateway once, when the instance's count moves, and never for the channel's own answer")]
    public async Task PollAsync_WithRenewal_ShouldRenewOnce()
    {
        // Arrange
        await using var instance = new Instance();
        var gateway = new RecordingPublisher();
        await instance.PublishAsync(gateway);
        await instance.Channel.PollAsync(gateway, CancellationToken.None);
        int before = gateway.Renewals;

        // Act
        (await instance.Client.RenewAsync(instance.ControlPlane.Manifest)).ShouldBeTrue();
        await instance.Channel.PollAsync(gateway, CancellationToken.None);
        await instance.Channel.PollAsync(gateway, CancellationToken.None);

        // Assert
        before.ShouldBe(0);
        gateway.Renewals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal the gateway does not know clears the answer, so the instance finds itself unknown and publishes again")]
    public async Task PollAsync_WithUnknownRenewal_ShouldSendTheInstanceToPublishAgain()
    {
        // Arrange
        await using var instance = new Instance();
        var gateway = new RecordingPublisher();
        await instance.PublishAsync(gateway);
        gateway.Known = false;

        // Act
        (await instance.Client.RenewAsync(instance.ControlPlane.Manifest)).ShouldBeTrue();
        await instance.Channel.PollAsync(gateway, CancellationToken.None);
        bool renewed = await instance.Client.RenewAsync(instance.ControlPlane.Manifest);
        gateway.Known = true;
        await instance.PublishAsync(gateway);

        // Assert
        renewed.ShouldBeFalse();
        gateway.Published.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "The instance client reaches the control plane on the published port once it serves, and gets no answer before")]
    public async Task ExecuteAsync_ThroughThePublishedPort_ShouldReachTheControlPlane()
    {
        // Arrange
        await using var instance = new Instance();
        var gateway = new RecordingPublisher();
        await instance.PublishAsync(gateway);
        await using DockerInfrastructure infrastructure = DockerInfrastructureTests.Create(instance.Engine);
        IApplicationGatewayInstanceClient client = infrastructure;
        ApplicationInstanceManifest manifest = gateway.Published.ShouldHaveSingleItem();
        JsonApplicationContentSerializer json = JsonApplicationContentSerializer.Create(TestJsonContext.Default);

        // Act
        HttpRequestException early = await Should.ThrowAsync<HttpRequestException>(() => client.ExecuteAsync(manifest, "greeting", ApplicationActionContext.Create(ApplicationActionOperation.Read, json)).AsTask());
        await instance.Client.StartAsync(instance.ControlPlane);
        ApplicationActionResult write = await client.ExecuteAsync(manifest, "greeting", ApplicationActionContext.Create(ApplicationActionOperation.Write, json, json.Serialize(new Greeting("hello docker"))));
        ApplicationActionResult restore = await client.RestoreAsync(manifest, ApplicationActionSnapshot.Create("greeting", json.Serialize(new Greeting("restored"))));
        ApplicationActionResult read = await client.ExecuteAsync(manifest, "greeting", ApplicationActionContext.Create(ApplicationActionOperation.Read, json));
        ApplicationActionResult missing = await client.ExecuteAsync(manifest, "log-level", ApplicationActionContext.Create(ApplicationActionOperation.Read, json));

        // Assert
        early.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        write.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        restore.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        json.Deserialize<Greeting>(read.Output).ShouldNotBeNull().Text.ShouldBe("restored");
        missing.Status.ShouldBe(ApplicationActionStatus.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "A channel receives for one gateway at a time")]
    public async Task Start_Twice_ShouldThrow()
    {
        // Arrange
        using var http = new HttpClient();
        var channel = new DockerChannel(new FakeDockerEngine(), "cohesion", 9090, "localhost", http);
        channel.Start(new RecordingPublisher());

        try
        {
            // Act & Assert
            Should.Throw<InvalidOperationException>(() => channel.Start(new RecordingPublisher()));
        }
        finally
        {
            await channel.StopAsync();
        }
    }

    // A gateway that records what its channel hands it.
    private sealed class RecordingPublisher : IApplicationInstancePublisher
    {
        private int _renewals;

        public ConcurrentQueue<ApplicationInstanceManifest> Published { get; } = new();

        public int Renewals => Volatile.Read(ref _renewals);

        public string? Refusal { get; set; }

        public bool Known { get; set; } = true;

        public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
        {
            if (Refusal is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            Published.Enqueue(manifest);
            return ValueTask.FromResult(new ApplicationInstancePublication([], _lease));
        }

        public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renewals);
            return ValueTask.FromResult(Known);
        }
    }

    // One instance in this process, as the Docker platform starts it: its client from UseDocker() in
    // the environment the gateway gives its container, and the container the fake engine lists.
    private sealed class Instance : IAsyncDisposable
    {
        public const string Container = "cohesion-orders-api-01234567-0";

        public Instance()
        {
            int port = GetFreePort();
            Resource = new TestResource();
            ControlPlane = Resource.CreateControlPlane();
            using (AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
            {
                [AppEnvironment.Variables.Gateway] = "docker",
                [AppEnvironment.Variables.ManifestEndpoint] = "docker:///networks/cohesion",
                ["DOCKER_CONTAINER_NAME"] = Container,
                ["DOCKER_CONTROL_PLANE_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, CancellationToken.None)))
            {
                Client = new TestOptions().UseDocker().GetGatewayClient().ShouldNotBeNull();
            }

            // In this process the client listens on the host port itself, so the engine publishes the
            // container's control-plane port on the same port.
            Port = port;
            Engine = new FakeDockerEngine();
            Engine.Add(Container, DockerWorkload.SelectNetwork("cohesion"), port, port);
            Http = new HttpClient();
            Channel = new DockerChannel(Engine, "cohesion", port, "localhost", Http);
        }

        public int Port { get; }

        public TestResource Resource { get; }

        public IApplicationInstanceControlPlane ControlPlane { get; }

        public IApplicationInstanceGatewayClient Client { get; }

        public FakeDockerEngine Engine { get; }

        public HttpClient Http { get; }

        public DockerChannel Channel { get; }

        public async Task PublishAsync(IApplicationInstancePublisher gateway)
        {
            Task<ApplicationInstancePublication> publishing = Client.PublishAsync(ControlPlane.Manifest).AsTask();
            await PollUntilAsync(gateway, () => publishing.IsCompleted);
            await publishing;
        }

        // The client opens its port as it publishes; the channel polls until the condition holds.
        public async Task PollUntilAsync(IApplicationInstancePublisher gateway, Func<bool> condition)
        {
            for (int attempt = 0; !condition.Invoke(); attempt++)
            {
                attempt.ShouldBeLessThan(100);
                await Channel.PollAsync(gateway, CancellationToken.None);
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            Http.Dispose();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class TestOptions : ApplicationInstanceOptions
    {
    }
}
