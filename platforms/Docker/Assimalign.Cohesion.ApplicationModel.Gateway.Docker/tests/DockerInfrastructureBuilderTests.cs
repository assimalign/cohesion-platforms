using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerInfrastructureBuilderTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - Infrastructure builder: ";

    private const string image = "cohesion-docker-web:e2e";

    [Fact(DisplayName = DisplayPrefix + "Build creates the infrastructure for the engine and network its options name")]
    public async Task Build_WithOptions_ShouldCreateTheInfrastructure()
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder();
        builder.Options.Endpoint = "tcp://127.0.0.1:1";
        builder.Options.Network = "shop";

        // Act
        await using DockerInfrastructure infrastructure = builder.Build();

        // Assert
        infrastructure.Platform.ShouldBe((PlatformName)"docker");
        infrastructure.Network.ShouldBe("shop");
        infrastructure.Endpoint.ShouldBe("tcp://127.0.0.1:1");
    }

    [Fact(DisplayName = DisplayPrefix + "Build copies the options: changing them later changes nothing")]
    public async Task Build_ThenChangedOptions_ShouldKeepTheBuiltOnes()
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder();
        builder.Options.Endpoint = "tcp://127.0.0.1:1";
        builder.Options.Network = "shop";
        await using DockerInfrastructure infrastructure = builder.Build();

        // Act
        builder.Options.Network = "storefront";

        // Assert
        infrastructure.Network.ShouldBe("shop");
    }

    [Theory(DisplayName = DisplayPrefix + "Build refuses a network that is not a DNS label, a port out of range, an unknown pull policy, or an endpoint the docker CLI does not accept")]
    [InlineData("tcp://127.0.0.1:1", "Shop", 8080, 9090, "IfNotPresent")]
    [InlineData("tcp://127.0.0.1:1", "shop", 0, 9090, "IfNotPresent")]
    [InlineData("tcp://127.0.0.1:1", "shop", 8080, 8080, "IfNotPresent")]
    [InlineData("tcp://127.0.0.1:1", "shop", 8080, 9090, "Sometimes")]
    [InlineData("ftp://127.0.0.1:1", "shop", 8080, 9090, "IfNotPresent")]
    public void Build_WithInvalidOptions_ShouldThrow(string endpoint, string network, int endpointPort, int controlPlanePort, string pullPolicy)
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder();
        builder.Options.Endpoint = endpoint;
        builder.Options.Network = network;
        builder.Options.EndpointPort = endpointPort;
        builder.Options.ControlPlanePort = controlPlanePort;
        builder.Options.ImagePullPolicy = pullPolicy;

        // Act & Assert
        Should.Throw<ArgumentException>(builder.Build);
    }

    [Fact(DisplayName = DisplayPrefix + "AddImage refuses a resource that already has an image, an empty image, or a missing name, when it is called")]
    public void AddImage_WithInvalidBinding_ShouldThrow()
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder().AddImage("Web", "orders-api", image);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", "orders-api", "cohesion-docker-other:e2e"));
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", "storefront", " "));
        Should.Throw<ArgumentException>(() => builder.AddImage(default, "storefront", image));
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", default, image));
    }

    [Fact(DisplayName = DisplayPrefix + "Build binds every image it was given to the infrastructure")]
    public async Task Build_WithImages_ShouldBindThem()
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder()
            .AddImage("Web", "orders-api", image)
            .AddImage("Web", "storefront", image);
        builder.Options.Endpoint = "tcp://127.0.0.1:1";

        // Act
        await using DockerInfrastructure infrastructure = builder.Build();

        // Assert: a bound resource takes no second image.
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", image));
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "storefront", image));
        infrastructure.AddImage("Web", "catalog", image).ShouldBeSameAs(infrastructure);
    }

    [Fact(DisplayName = DisplayPrefix + "Each Build creates an infrastructure of its own")]
    public async Task Build_Twice_ShouldCreateTwoInfrastructures()
    {
        // Arrange
        var builder = new DockerInfrastructureBuilder();
        builder.Options.Endpoint = "tcp://127.0.0.1:1";

        // Act
        await using DockerInfrastructure first = builder.Build();
        await using DockerInfrastructure second = builder.Build();

        // Assert
        second.ShouldNotBeSameAs(first);
    }
}
