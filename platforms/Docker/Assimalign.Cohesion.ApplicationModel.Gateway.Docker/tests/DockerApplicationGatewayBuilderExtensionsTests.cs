using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerApplicationGatewayBuilderExtensionsTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - UseDocker: ";

    private const string image = "cohesion-docker-web:e2e";

    [Fact(DisplayName = DisplayPrefix + "The callback runs, and the infrastructure is created, only when the gateway is built")]
    public async Task UseDocker_WithCallback_ShouldCreateTheInfrastructureOnBuild()
    {
        // Arrange
        int calls = 0;
        DockerInfrastructureBuilder? captured = null;
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseDocker(docker =>
        {
            calls++;
            captured = docker;
            docker.Options.Endpoint = "tcp://127.0.0.1:1";
            docker.Options.Network = "shop";
            docker.AddImage("Web", "orders-api", image);
        });
        builder.AddResource("Web", "orders-api");

        // Act
        int before = calls;
        await using ApplicationGateway gateway = builder.Build();

        // Assert
        before.ShouldBe(0);
        calls.ShouldBe(1);
        DockerInfrastructure infrastructure = captured.ShouldNotBeNull().Built.ShouldNotBeNull();
        infrastructure.Network.ShouldBe("shop");
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", image));
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway owns the infrastructure the callback builds: disposing the gateway disposes it")]
    public async Task UseDocker_WithCallback_ShouldBeDisposedWithTheGateway()
    {
        // Arrange
        DockerInfrastructureBuilder? captured = null;
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseDocker(docker =>
        {
            captured = docker;
            docker.Options.Endpoint = "tcp://127.0.0.1:1";
        });
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.Build();
        DockerInfrastructure infrastructure = captured.ShouldNotBeNull().Built.ShouldNotBeNull();

        // Act
        await gateway.DisposeAsync();

        // Assert
        Should.Throw<ObjectDisposedException>(() => infrastructure.AddImage("Web", "orders-api", image));
    }

    [Fact(DisplayName = DisplayPrefix + "What the callback or the infrastructure's creation throws, the gateway's build throws")]
    public void UseDocker_WithInvalidConfiguration_ShouldFailTheBuild()
    {
        // Arrange
        ApplicationGatewayBuilder invalidNetwork = ApplicationGateway.CreateBuilder("shop").UseDocker(docker =>
        {
            docker.Options.Endpoint = "tcp://127.0.0.1:1";
            docker.Options.Network = "Shop";
        });
        ApplicationGatewayBuilder duplicateImage = ApplicationGateway.CreateBuilder("shop").UseDocker(docker => docker
            .AddImage("Web", "orders-api", image)
            .AddImage("Web", "orders-api", image));

        // Act & Assert
        Should.Throw<ArgumentException>(invalidNetwork.Build);
        Should.Throw<ArgumentException>(duplicateImage.Build);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway borrows an infrastructure it is given: disposing the gateway leaves it to its caller")]
    public async Task UseDocker_WithInfrastructure_ShouldLeaveItUndisposed()
    {
        // Arrange
        await using DockerInfrastructure infrastructure = DockerInfrastructure.Create(new DockerInfrastructureOptions { Endpoint = "tcp://127.0.0.1:1" });
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseDocker(infrastructure);
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.Build();

        // Act
        await gateway.DisposeAsync();

        // Assert
        infrastructure.AddImage("Web", "orders-api", image).ShouldBeSameAs(infrastructure);
    }

    [Fact(DisplayName = DisplayPrefix + "A null callback or infrastructure is refused")]
    public void UseDocker_WithNull_ShouldThrow()
    {
        // Arrange
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop");

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => builder.UseDocker((Action<DockerInfrastructureBuilder>)null!));
        Should.Throw<ArgumentNullException>(() => builder.UseDocker((DockerInfrastructure)null!));
    }
}
