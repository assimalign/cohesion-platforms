using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerWorkloadTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - Workload: ";

    private static readonly DockerInfrastructureOptions _options = new();

    [Fact(DisplayName = DisplayPrefix + "A container receives the runtime contract, its own name, and its settings as its environment")]
    public void CreateContainer_ShouldHandTheContainerTheRuntimeContract()
    {
        // Arrange
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();
        DeploymentId deployment = DeploymentId.New();

        // Act
        DockerContainerCreateRequest container = DockerWorkload.CreateContainer(model, resource, Settings(("Orders:Url", "http://orders-api:8080")), deployment, "cohesion-docker-web:e2e", "rev", "0123456789abcdef", 0, "cohesion-orders-api-01234567-0", "cohesion", _options);

        // Assert
        Dictionary<string, string> environment = container.Env.ShouldNotBeNull().Select(static variable => variable.Split('=', 2)).ToDictionary(static pair => pair[0], static pair => pair[1]);
        environment[AppEnvironment.Variables.Application].ShouldBe("local");
        environment[AppEnvironment.Variables.Gateway].ShouldBe("docker");
        environment[AppEnvironment.Variables.Resource].ShouldBe("orders-api");
        environment[AppEnvironment.Variables.ManifestEndpoint].ShouldBe("docker:///networks/cohesion");
        environment[AppEnvironment.Variables.Deployment].ShouldBe(deployment.ToString());
        environment[AppEnvironment.Variables.Endpoint("http", "HOST")].ShouldBe("0.0.0.0");
        environment[AppEnvironment.Variables.Endpoint("http", "PORT")].ShouldBe("8080");
        environment[AppEnvironment.Variables.Configuration("Orders:Url")].ShouldBe("http://orders-api:8080");
        environment["DOCKER_CONTAINER_NAME"].ShouldBe("cohesion-orders-api-01234567-0");
        environment["DOCKER_CONTROL_PLANE_PORT"].ShouldBe("9090");
        container.Image.ShouldBe("cohesion-docker-web:e2e");
        container.Labels.ShouldNotBeNull()[DockerWorkload.DeploymentLabel].ShouldBe(deployment.ToString());
        container.Labels[DockerWorkload.NetworkLabel].ShouldBe("cohesion");
        container.Labels[DockerWorkload.ResourceLabel].ShouldBe("orders-api");
        container.Labels[DockerWorkload.WorkloadLabel].ShouldBe("0123456789abcdef");
    }

    [Fact(DisplayName = DisplayPrefix + "A container runs under the engine's init, restarts unless stopped, joins the network under the resource's name, and publishes both ports on loopback")]
    public void CreateContainer_ShouldShapeTheContainer()
    {
        // Arrange
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();

        // Act
        DockerContainerCreateRequest container = DockerWorkload.CreateContainer(model, resource, Settings(), DeploymentId.New(), "web:1", "rev", "0123456789abcdef", 1, "name", "cohesion", _options);

        // Assert
        DockerHostConfig host = container.HostConfig.ShouldNotBeNull();
        host.Init.ShouldBe(true);
        host.RestartPolicy.ShouldNotBeNull().Name.ShouldBe("unless-stopped");
        host.NetworkMode.ShouldBe("cohesion");
        host.PortBindings.ShouldNotBeNull().Keys.ShouldBe(["8080/tcp", "9090/tcp"], ignoreOrder: true);
        foreach (DockerPortBinding[]? bindings in host.PortBindings.Values)
        {
            DockerPortBinding binding = bindings.ShouldNotBeNull().ShouldHaveSingleItem();
            binding.HostIp.ShouldBe("127.0.0.1");
            binding.HostPort.ShouldBe(string.Empty);
        }

        container.NetworkingConfig.ShouldNotBeNull().EndpointsConfig.ShouldNotBeNull()["cohesion"].Aliases.ShouldBe(["orders-api"]);
        container.Labels.ShouldNotBeNull()[DockerWorkload.ReplicaLabel].ShouldBe("1");
    }

    [Fact(DisplayName = DisplayPrefix + "The revision follows the image and ports, and the workload follows the revision, replicas, and settings")]
    public void GetRevisionAndWorkload_ShouldFollowWhatRuns()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();
        (_, ApplicationResourceManifest replicated) = CreateResource("3");
        string revision = DockerWorkload.GetRevision("web:1", 8080, 9090);

        // Act
        string first = DockerWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "2")), null);
        string reordered = DockerWorkload.GetWorkload(revision, resource, Settings(("B", "2"), ("A", "1")), null);
        string changedSetting = DockerWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "3")), null);
        string changedReplicas = DockerWorkload.GetWorkload(revision, replicated, Settings(("A", "1"), ("B", "2")), null);
        string changedImage = DockerWorkload.GetWorkload(DockerWorkload.GetRevision("web:2", 8080, 9090), resource, Settings(("A", "1"), ("B", "2")), null);

        // Assert
        DockerWorkload.GetRevision("web:1", 8080, 9090).ShouldBe(revision);
        DockerWorkload.GetRevision("web:1", 8081, 9090).ShouldNotBe(revision);
        reordered.ShouldBe(first);
        changedSetting.ShouldNotBe(first);
        changedReplicas.ShouldNotBe(first);
        changedImage.ShouldNotBe(first);
        DockerWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "2")), "Staging").ShouldNotBe(first);
    }

    [Fact(DisplayName = DisplayPrefix + "Settings whose values contain the separators never hash like another set of settings")]
    public void GetWorkload_WithSeparatorsInValues_ShouldNotCollide()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();
        string revision = DockerWorkload.GetRevision("web:1", 8080, 9090);

        // Act: without length prefixes, both read "A=x\nB=y\n".
        string one = DockerWorkload.GetWorkload(revision, resource, Settings(("A", "x\nB=y")), null);
        string two = DockerWorkload.GetWorkload(revision, resource, Settings(("A", "x"), ("B", "y")), null);

        // Assert
        one.ShouldNotBe(two);
    }

    [Theory(DisplayName = DisplayPrefix + "A pull names one tag: the reference's own, or latest for one with neither tag nor digest")]
    [InlineData("orders-api", "/images/create?fromImage=orders-api&tag=latest")]
    [InlineData("cohesion-docker-web:e2e", "/images/create?fromImage=cohesion-docker-web&tag=e2e")]
    [InlineData("localhost:5001/web", "/images/create?fromImage=localhost%3A5001%2Fweb&tag=latest")]
    [InlineData("localhost:5001/shop/web:1.2", "/images/create?fromImage=localhost%3A5001%2Fshop%2Fweb&tag=1.2")]
    [InlineData("web@sha256:abc", "/images/create?fromImage=web%40sha256%3Aabc")]
    public void GetPullPath_ShouldNameOneTag(string image, string expected)
    {
        // Act & Assert
        DockerEngineClient.GetPullPath(image).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "An image is inspected by its reference as the docker CLI puts it in the path")]
    [InlineData("cohesion-docker-web:e2e", "/images/cohesion-docker-web:e2e/json")]
    [InlineData("localhost:5001/shop/web:1.2", "/images/localhost:5001/shop/web:1.2/json")]
    [InlineData("web@sha256:abc", "/images/web@sha256:abc/json")]
    [InlineData("we b", "/images/we%20b/json")]
    public void GetImageInspectPath_ShouldKeepTheReference(string image, string expected)
    {
        // Act & Assert
        DockerEngineClient.GetImageInspectPath(image).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "WithDockerReplicas sets how many containers run, on the docker platform only")]
    public void GetReplicas_WithDockerReplicas_ShouldReadIt()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("local");
        builder.AddResource("Web", "orders-api").WithDockerReplicas(2).WithDockerReplicas(3);

        // Act
        ApplicationResourceManifest resource = builder.Build().Manifest.Resources.Single();

        // Assert
        DockerWorkload.GetReplicas(resource).ShouldBe(3);
        resource.Extensions.ShouldHaveSingleItem().Platform.ShouldBe((PlatformName)"docker");
    }

    [Fact(DisplayName = DisplayPrefix + "WithDockerReplicas refuses fewer than one instance")]
    public void WithDockerReplicas_WithZero_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDefinition resource = ApplicationModel.CreateBuilder("local").AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => resource.WithDockerReplicas(0));
    }

    [Theory(DisplayName = DisplayPrefix + "A replicas extension that is not a whole number of at least 1 is refused")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("\"two\"")]
    public void GetReplicas_WithInvalidExtension_ShouldThrow(string replicas)
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource(replicas);

        // Act & Assert
        Should.Throw<ArgumentException>(() => DockerWorkload.GetReplicas(resource));
    }

    [Theory(DisplayName = DisplayPrefix + "A container that ended, keeps restarting, or did not start cannot run; one that runs can")]
    [InlineData("running", 0, 0, null, null)]
    [InlineData("running", 1, 0, null, null)]
    [InlineData("exited", 0, 134, null, "exited with code 134")]
    [InlineData("dead", 0, 137, null, "exited with code 137")]
    [InlineData("restarting", 2, 1, null, "restarted 2 times; it last exited with code 1")]
    [InlineData("running", 3, 0, null, "restarted 3 times")]
    [InlineData("created", 0, 0, "no such file", "the engine could not start it: no such file")]
    public void GetFailure_ShouldReadTheEnginesReason(string status, int restarts, int exitCode, string? error, string? expected)
    {
        // Arrange
        var container = new DockerContainerInspectResponse
        {
            RestartCount = restarts,
            State = new DockerContainerState { Status = status, Running = status == "running", ExitCode = exitCode, Error = error },
        };

        // Act
        string? failure = DockerWorkload.GetFailure(container);

        // Assert
        if (expected is null)
        {
            failure.ShouldBeNull();
        }
        else
        {
            failure.ShouldNotBeNull().ShouldStartWith(expected, Case.Sensitive);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's containers are named for the network, the resource, the workload, and the replica, and answer at the resource's name")]
    public void GetContainerNameAndEndpoint_ShouldNameTheResource()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();

        // Act & Assert
        DockerWorkload.GetContainerName("cohesion", resource, "0123456789abcdef", 1).ShouldBe("cohesion-orders-api-01234567-1");
        DockerWorkload.GetEndpoint(resource, _options).ShouldBe("http://orders-api:8080");
    }

    [Fact(DisplayName = DisplayPrefix + "The published host port is read from a listing and from an inspection")]
    public void GetHostPort_ShouldReadThePublishedPort()
    {
        // Arrange
        var summary = new DockerContainerSummary
        {
            Ports =
            [
                new DockerPortSummary { PrivatePort = 8080, PublicPort = 41000, Type = "tcp" },
                new DockerPortSummary { PrivatePort = 9090, PublicPort = 42000, Type = "tcp" },
            ],
        };
        var inspected = new DockerContainerInspectResponse
        {
            NetworkSettings = new DockerNetworkSettings
            {
                Ports = new Dictionary<string, DockerPortBinding[]?> { ["9090/tcp"] = [new DockerPortBinding { HostIp = "127.0.0.1", HostPort = "42000" }], ["8080/tcp"] = null },
            },
        };

        // Act & Assert
        DockerWorkload.GetHostPort(summary, 9090).ShouldBe(42000);
        DockerWorkload.GetHostPort(summary, 7070).ShouldBeNull();
        DockerWorkload.GetHostPort(inspected, 9090).ShouldBe(42000);
        DockerWorkload.GetHostPort(inspected, 8080).ShouldBeNull();
    }

    private static (IApplicationModel Model, ApplicationResourceManifest Resource) CreateResource(string? replicas = null)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("local");
        IApplicationResourceDefinition definition = builder.AddResource("Web", "orders-api");
        if (replicas is not null)
        {
            using JsonDocument value = JsonDocument.Parse(replicas);
            definition.WithExtension("docker", "replicas", value.RootElement.Clone());
        }

        IApplicationModel model = builder.Build();
        return (model, model.Manifest.Resources.Single());
    }

    private static Dictionary<SettingName, string> Settings(params (string Name, string Value)[] settings)
    {
        return settings.ToDictionary(static setting => (SettingName)setting.Name, static setting => setting.Value);
    }
}
