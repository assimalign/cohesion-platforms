using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// The Docker platform end to end, on a real engine: the gateway runs here, on the engine's host, and
/// reaches its instances, as a developer reaches their endpoints, only through the ports the engine
/// publishes on that host's loopback.
/// </summary>
/// <remarks>
/// Prerequisites: an engine (Docker, or a Podman machine), and the fixture program's image in its
/// store from <c>scripts/Publish-CohesionDockerFixtures.ps1</c>. Set <c>COHESION_DOCKER_E2E=1</c> to run it.
/// </remarks>
public class DockerEndToEndTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - End to end on the engine: ";

    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(2);

    [DockerEngineFact(DisplayName = DisplayPrefix + "The gateway deploys its resources, their containers publish, the storefront reaches orders-api at its endpoint, and a write reaches every instance")]
    public async Task StartAsync_OnEngine_ShouldDeployManageAndServeTheResources()
    {
        // Arrange: one image runs both resources. orders-api runs two instances, and the storefront's
        // setting is orders-api's endpoint, its name on the network.
        await using DockerEngine engine = DockerEngine.Connect();
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.StartupTimeout = _startupTimeout;
        builder.UseDocker(docker => engine.Configure(docker)
            .AddImage("Web", "orders-api", engine.Image)
            .AddImage("Web", "storefront", engine.Image));
        IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api").WithDockerReplicas(2);
        builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);
        JsonApplicationContentSerializer json = JsonApplicationContentSerializer.Create(TestJsonContext.Default);

        // Act: the storefront answers on its published port once its server listens, which follows its
        // publication; the second orders-api container may publish after the gateway started.
        await using GatewayRun run = await GatewayRun.StartAsync(builder.Build(), _startupTimeout + TimeSpan.FromMinutes(1));
        int storefront = await GetHostPortAsync(engine, "storefront", new DockerInfrastructureOptions().EndpointPort);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{storefront}/"), Timeout = TimeSpan.FromSeconds(10) };
        string answer = await Eventually.GetAsync(() => http.GetStringAsync("/"), static exception => exception is HttpRequestException or TaskCanceledException, TimeSpan.FromMinutes(1));
        IApplicationGatewayDeployment ordersApi = run.Gateway.Resources.Single(static resource => resource.Name.Value == "orders-api");
        await Eventually.WaitUntilAsync(() => ordersApi.Instances.Count == 2, TimeSpan.FromMinutes(1));
        ApplicationActionResult write = await run.Gateway.ExecuteAsync(
            "Web",
            "orders-api",
            "greeting",
            ApplicationActionContext.Create(ApplicationActionOperation.Write, json, json.Serialize(new Greeting("hello docker"))));

        // Assert: the write ran on one instance and reached the other, by the gateway's restore or, when
        // the restore came before that instance's control plane opened, by its next publication, after
        // which both are registered again.
        answer.ShouldBe("storefront -> orders-api");
        ordersApi.Outputs[OutputName.Endpoint].ShouldBe("http://orders-api:8080");
        write.Status.ShouldBe(ApplicationActionStatus.Succeeded);

        IReadOnlyList<DockerContainerSummary> containers = await engine.Client.ListContainersAsync(DockerWorkload.SelectNetwork(engine.Network), CancellationToken.None);
        containers.Count.ShouldBe(3);
        foreach (DockerContainerSummary container in containers)
        {
            DockerContainerInspectResponse inspected = (await engine.Client.InspectContainerAsync(container.Id!, CancellationToken.None)).ShouldNotBeNull();
            inspected.State.ShouldNotBeNull().Running.ShouldBeTrue();
        }

        // Each instance holds the write: the test reads it from every one with a client of its own, as
        // the gateway owns its infrastructure.
        await Eventually.WaitUntilAsync(() => ordersApi.Instances.Count == 2, TimeSpan.FromMinutes(1));
        await using DockerInfrastructure reader = engine.CreateInfrastructure();
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
            json.Deserialize<Greeting>(read.Output).ShouldNotBeNull().Text.ShouldBe("hello docker");
        }
    }

    [DockerEngineFact(DisplayName = DisplayPrefix + "A gateway that runs again over the network adopts the containers the last run left, and their instances publish to it")]
    public async Task StartAsync_AfterAnEarlierRun_ShouldAdoptTheRunningContainers()
    {
        // Arrange: a first run deploys orders-api and stops, leaving its container running. Each run's
        // gateway owns an infrastructure of its own, which goes with the run.
        await using DockerEngine engine = DockerEngine.Connect();
        string first;
        await using (GatewayRun run = await GatewayRun.StartAsync(CreateGateway(engine), _startupTimeout + TimeSpan.FromMinutes(1)))
        {
            first = (await engine.Client.ListContainersAsync(DockerWorkload.SelectNetwork(engine.Network), CancellationToken.None)).ShouldHaveSingleItem().Id!;
        }

        // Act: a second run, without the first one's record, plans the same workload.
        await using GatewayRun adopted = await GatewayRun.StartAsync(CreateGateway(engine), _startupTimeout + TimeSpan.FromMinutes(1));
        IApplicationGatewayDeployment ordersApi = adopted.Gateway.Resources.ShouldHaveSingleItem();

        // Assert: the container is the first run's, and its instance, unknown to the new run, renewed,
        // was sent to publish again, and is registered.
        await Eventually.WaitUntilAsync(() => ordersApi.Instances.Count == 1, TimeSpan.FromMinutes(1));
        (await engine.Client.ListContainersAsync(DockerWorkload.SelectNetwork(engine.Network), CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(first);
        ordersApi.Outputs[OutputName.Endpoint].ShouldBe("http://orders-api:8080");

        static ApplicationGateway CreateGateway(DockerEngine engine)
        {
            ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
            builder.StartupTimeout = _startupTimeout;
            builder.LeaseDuration = TimeSpan.FromSeconds(6);
            builder.UseDocker(docker => engine.Configure(docker).AddImage("Web", "orders-api", engine.Image));
            builder.AddResource("Web", "orders-api");
            return builder.Build();
        }
    }

    [DockerEngineFact(DisplayName = DisplayPrefix + "A container whose program fails its start fails the gateway's start at once, with the engine's reason")]
    public async Task StartAsync_WithFailingProgram_ShouldFailWithTheContainersExit()
    {
        // Arrange: the fixture throws before it publishes when its Fixture:Failure setting is set, so
        // its container exits and the engine restarts it. The gateway borrows this infrastructure, which
        // the test disposes after the failed run.
        await using DockerEngine engine = DockerEngine.Connect();
        await using DockerInfrastructure infrastructure = engine.CreateInfrastructure();
        infrastructure.AddImage("Web", "broken", engine.Image);

        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.StartupTimeout = _startupTimeout;
        builder.UseDocker(infrastructure);
        builder.AddResource("Web", "broken").WithSetting("Fixture:Failure", "the fixture fails on purpose");

        // Act: the watch sees the container end, well inside the startup window.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception failure = await Should.ThrowAsync<Exception>(() => GatewayRun.StartAsync(builder.Build(), _startupTimeout + TimeSpan.FromMinutes(1)));

        // Assert: the run names the container, the engine's reason, and the program's own last words;
        // under the engine's init the program's abort ends the container instead of spinning as its PID 1.
        System.Diagnostics.Stopwatch.GetElapsedTime(start).ShouldBeLessThan(_startupTimeout);
        failure.ToString().ShouldContain("cannot run", Case.Sensitive);
        failure.ToString().ShouldContain("the fixture fails on purpose", Case.Sensitive);
    }

    // The loopback port the engine published a container of the resource's port on.
    private static async Task<int> GetHostPortAsync(DockerEngine engine, string resource, int port)
    {
        Dictionary<string, string> selector = DockerWorkload.SelectNetwork(engine.Network);
        selector[DockerWorkload.ResourceLabel] = resource;
        DockerContainerSummary container = (await engine.Client.ListContainersAsync(selector, CancellationToken.None)).ShouldHaveSingleItem();
        DockerContainerInspectResponse inspected = (await engine.Client.InspectContainerAsync(container.Id!, CancellationToken.None)).ShouldNotBeNull();
        return DockerWorkload.GetHostPort(inspected, port).ShouldNotBeNull();
    }
}
