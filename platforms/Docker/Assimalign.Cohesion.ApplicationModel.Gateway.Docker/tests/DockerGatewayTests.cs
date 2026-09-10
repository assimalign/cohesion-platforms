using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Name: Should be the stable identity 'docker'")]
    public void Name_OnGateway_ShouldBeDocker()
    {
        // Arrange
        var gateway = new DockerGateway();

        // Act
        string name = gateway.Name.Value;

        // Assert
        name.ShouldBe("docker");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - UseDockerGateway: Should apply Docker host and image archive arguments")]
    public void UseDockerGateway_OnPlatformArguments_ShouldApplyDockerOptions()
    {
        // Arrange
        string image = $"registry.example/worker@sha256:{new string('a', 64)}";
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        builder.AddResource(CreateManifest(image));
        Uri? endpoint = null;
        string? archive = null;

        // Act
        IApplication application = builder
            .UseDockerGateway(
                [
                    "--docker-host=http://127.0.0.1:2375",
                    $"--image-archive={image}=C:\\images\\worker.tar",
                ],
                options =>
                {
                    endpoint = options.EngineEndpoint;
                    archive = options.ImageArchives[image];
                })
            .Build();

        // Assert
        application.Model.GatewayIdentity.Value.ShouldBe("docker");
        endpoint.ShouldBe(new Uri("http://127.0.0.1:2375", UriKind.Absolute));
        archive.ShouldBe("C:\\images\\worker.tar");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - UseDockerGateway: Should throw ArgumentNullException for null configure")]
    public void UseDockerGateway_OnNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(
            () => builder.UseDockerGateway((Action<DockerGatewayOptions>)null!));

        // Assert
        exception.ParamName.ShouldBe("configure");
    }

    private static ResourceManifest CreateManifest(string image) => new()
    {
        Name = "worker",
        Kind = "Worker",
        Application = "appa",
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Assimalign.Cohesion.Test.Application",
            Image = image,
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "http",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "http",
            Path = "/cohesion/v1",
        },
    };
}
