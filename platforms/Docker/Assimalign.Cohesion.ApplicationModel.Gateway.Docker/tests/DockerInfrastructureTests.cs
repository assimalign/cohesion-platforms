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

public class DockerInfrastructureTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - Infrastructure: ";

    private const string image = "cohesion-docker-web:e2e";

    [Fact(DisplayName = DisplayPrefix + "The infrastructure is the docker platform on its network, at the engine it resolved")]
    public async Task Create_WithEndpoint_ShouldNameThePlatformNetworkAndEngine()
    {
        // Act
        await using DockerInfrastructure infrastructure = DockerInfrastructure.Create(new DockerInfrastructureOptions { Endpoint = "tcp://127.0.0.1:1", Network = "shop" });

        // Assert
        infrastructure.Platform.ShouldBe((PlatformName)"docker");
        infrastructure.Network.ShouldBe("shop");
        infrastructure.Endpoint.ShouldBe("tcp://127.0.0.1:1");
    }

    [Theory(DisplayName = DisplayPrefix + "A network that is not a DNS label, a port out of range, or an unknown pull policy is refused")]
    [InlineData("Shop", 8080, 9090, "IfNotPresent")]
    [InlineData("-shop", 8080, 9090, "IfNotPresent")]
    [InlineData("shop", 0, 9090, "IfNotPresent")]
    [InlineData("shop", 8080, 8080, "IfNotPresent")]
    [InlineData("shop", 8080, 9090, "Sometimes")]
    public void Create_WithInvalidOptions_ShouldThrow(string network, int endpointPort, int controlPlanePort, string pullPolicy)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => DockerInfrastructure.Create(new DockerInfrastructureOptions
        {
            Endpoint = "tcp://127.0.0.1:1",
            Network = network,
            EndpointPort = endpointPort,
            ControlPlanePort = controlPlanePort,
            ImagePullPolicy = pullPolicy,
        }));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource is bound to one image")]
    public async Task AddImage_WithBoundResource_ShouldThrow()
    {
        // Arrange
        await using DockerInfrastructure infrastructure = Create(new FakeDockerEngine());

        // Act & Assert
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", image));
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "storefront", " "));
    }

    [Fact(DisplayName = DisplayPrefix + "Planning creates what does not run, leaves what runs the workload, and updates what runs another")]
    public async Task PlanResourceAsync_ShouldFollowTheRunningContainers()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource(replicas: 2);
        Dictionary<SettingName, string> settings = Settings(("Greeting", "hello"));

        // Act
        ApplicationGatewayDeploymentPlan created = await provider.PlanResourceAsync(model, resource, settings);
        await provider.DeployResourceAsync(model, resource, settings, DeploymentId.New());
        ApplicationGatewayDeploymentPlan unchanged = await provider.PlanResourceAsync(model, resource, settings);
        ApplicationGatewayDeploymentPlan updated = await provider.PlanResourceAsync(model, resource, Settings(("Greeting", "welcome")));
        engine.Containers[0].Labels.Remove(DockerWorkload.ResourceLabel);
        ApplicationGatewayDeploymentPlan missing = await provider.PlanResourceAsync(model, resource, settings);

        // Assert: the revision is what the model does not carry, so a setting does not change it.
        created.Change.ShouldBe(ApplicationGatewayDeploymentChange.Create);
        unchanged.Change.ShouldBe(ApplicationGatewayDeploymentChange.None);
        updated.Change.ShouldBe(ApplicationGatewayDeploymentChange.Update);
        missing.Change.ShouldBe(ApplicationGatewayDeploymentChange.Update);
        unchanged.Revision.ShouldBe(created.Revision);
        updated.Revision.ShouldBe(created.Revision);
    }

    [Fact(DisplayName = DisplayPrefix + "Deploying runs one started container per replica on the network, pulls a missing image, and replaces the containers of the previous workload")]
    public async Task DeployResourceAsync_ShouldReplaceThePreviousContainers()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource(replicas: 2);
        DeploymentId first = DeploymentId.New();
        DeploymentId second = DeploymentId.New();

        // Act
        await provider.DeployResourceAsync(model, resource, Settings(("Greeting", "hello")), first);
        string[] before = [.. engine.Containers.Select(static container => container.Name)];
        await provider.DeployResourceAsync(model, resource, Settings(("Greeting", "welcome")), second);

        // Assert
        engine.Networks.ShouldContain("cohesion");
        engine.Pulled.ShouldBe([image]);
        before.Length.ShouldBe(2);
        FakeContainer[] after = [.. engine.Containers];
        after.Length.ShouldBe(2);
        after.ShouldAllBe(container => container.State == "running" && container.Labels[DockerWorkload.DeploymentLabel] == second.ToString());
        after.Select(static container => container.Name).ShouldNotContain(before[0]);
        after.Select(static container => container.Labels[DockerWorkload.ReplicaLabel]).ShouldBe(["0", "1"], ignoreOrder: true);
    }

    [Fact(DisplayName = DisplayPrefix + "Deploying again over a deployment that did not finish keeps its containers and creates the missing ones")]
    public async Task DeployResourceAsync_Again_ShouldKeepTheDeploymentsContainers()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        engine.Images.Add(image);
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource(replicas: 2);
        DeploymentId deployment = DeploymentId.New();
        await provider.DeployResourceAsync(model, resource, Settings(), deployment);
        string kept = engine.Containers[0].Name;
        await engine.RemoveContainerAsync(engine.Containers[1].Name, CancellationToken.None);

        // Act
        await provider.DeployResourceAsync(model, resource, Settings(), deployment);

        // Assert
        engine.Pulled.ShouldBeEmpty();
        engine.Containers.Count.ShouldBe(2);
        engine.Containers.Select(static container => container.Name).ShouldContain(kept);
    }

    [Fact(DisplayName = DisplayPrefix + "The watch fails a deployment whose container ended, with the engine's reason and the program's last output")]
    public async Task WatchResourceAsync_WithEndedContainer_ShouldThrow()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        await provider.DeployResourceAsync(model, resource, Settings(), deployment);
        FakeContainer container = engine.Containers.ShouldHaveSingleItem();
        container.State = "exited";
        container.ExitCode = 134;
        container.Logs = "Unhandled exception. System.InvalidOperationException: no orders\n";

        // Act
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => provider.WatchResourceAsync(model, resource, deployment).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        failure.Message.ShouldContain(container.Name, Case.Sensitive);
        failure.Message.ShouldContain("exited with code 134", Case.Sensitive);
        failure.Message.ShouldContain("no orders", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "The watch ends when a later deployment or a teardown removed the deployment's containers, and waits while they run")]
    public async Task WatchResourceAsync_WhenReplaced_ShouldReturn()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();
        DeploymentId deployment = DeploymentId.New();
        await provider.DeployResourceAsync(model, resource, Settings(), deployment);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => provider.WatchResourceAsync(model, resource, deployment, cancellation.Token).AsTask());
        await provider.TeardownResourceAsync(model, resource);
        await provider.WatchResourceAsync(model, resource, deployment).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        engine.Containers.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource that runs has its name on the network as its endpoint; one that does not has no outputs")]
    public async Task GetResourceOutputsAsync_ShouldNameTheEndpoint()
    {
        // Arrange
        var engine = new FakeDockerEngine();
        await using DockerInfrastructure infrastructure = Create(engine);
        IApplicationGatewayInfrastructureProvider provider = infrastructure;
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();

        // Act
        IReadOnlyDictionary<OutputName, string> none = await provider.GetResourceOutputsAsync(model, resource);
        await provider.DeployResourceAsync(model, resource, Settings(), DeploymentId.New());
        IReadOnlyDictionary<OutputName, string> outputs = await provider.GetResourceOutputsAsync(model, resource);

        // Assert
        none.ShouldBeEmpty();
        outputs[OutputName.Endpoint].ShouldBe("http://orders-api:8080");
    }

    [Fact(DisplayName = DisplayPrefix + "The instance client calls only Docker control-plane endpoints, of containers that publish them")]
    public async Task ExecuteAsync_WithForeignOrGoneControlPlane_ShouldThrow()
    {
        // Arrange
        await using DockerInfrastructure infrastructure = Create(new FakeDockerEngine());
        IApplicationGatewayInstanceClient client = infrastructure;
        ApplicationActionContext read = ApplicationActionContext.Create(ApplicationActionOperation.Read, JsonApplicationContentSerializer.Create());
        var foreign = new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), null, new Uri("http://127.0.0.1:9090/"), []);
        var gone = foreign with { ControlPlaneEndpoint = new Uri("docker:///networks/cohesion/containers/cohesion-orders-api-01234567-0:9090/") };

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => client.ExecuteAsync(foreign, "greeting", read).AsTask());
        await Should.ThrowAsync<HttpRequestException>(() => client.ExecuteAsync(gone, "greeting", read).AsTask());
    }

    internal static DockerInfrastructure Create(FakeDockerEngine engine, string host = "localhost")
    {
        return DockerInfrastructure.Create(new DockerInfrastructureOptions(), engine, host).AddImage("Web", "orders-api", image);
    }

    internal static (IApplicationModel Model, ApplicationResourceManifest Resource) CreateResource(int replicas = 1)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("local");
        IApplicationResourceDefinition definition = builder.AddResource("Web", "orders-api");
        if (replicas != 1)
        {
            definition.WithDockerReplicas(replicas);
        }

        IApplicationModel model = builder.Build();
        return (model, model.Manifest.Resources.Single());
    }

    private static Dictionary<SettingName, string> Settings(params (string Name, string Value)[] settings)
    {
        return settings.ToDictionary(static setting => (SettingName)setting.Name, static setting => setting.Value);
    }
}
