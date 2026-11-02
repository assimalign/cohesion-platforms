using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Docker;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Tests;

public class DockerProtocolTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Docker] - Protocol: ";

    [Fact(DisplayName = DisplayPrefix + "A gateway endpoint names the network the gateway runs")]
    public void GatewayEndpoint_RoundTrip_ShouldKeepTheNetwork()
    {
        // Act
        Uri endpoint = DockerProtocol.CreateGatewayEndpoint("cohesion");
        bool parsed = DockerProtocol.TryParseGatewayEndpoint(endpoint, out string? network);

        // Assert
        endpoint.AbsoluteUri.ShouldBe("docker:///networks/cohesion");
        parsed.ShouldBeTrue();
        network.ShouldBe("cohesion");
    }

    [Fact(DisplayName = DisplayPrefix + "A control-plane endpoint names the container and the port inside it")]
    public void ControlPlaneEndpoint_RoundTrip_ShouldKeepTheContainerAndPort()
    {
        // Act
        Uri endpoint = DockerProtocol.CreateControlPlaneEndpoint("cohesion", "cohesion-orders-api-1a2b3c4d-0", 9090);
        bool parsed = DockerProtocol.TryParseControlPlaneEndpoint(endpoint, out string? network, out string? container, out int port);

        // Assert
        endpoint.AbsoluteUri.ShouldBe("docker:///networks/cohesion/containers/cohesion-orders-api-1a2b3c4d-0:9090/");
        parsed.ShouldBeTrue();
        network.ShouldBe("cohesion");
        container.ShouldBe("cohesion-orders-api-1a2b3c4d-0");
        port.ShouldBe(9090);
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint of another form is not the platform's")]
    [InlineData("http://127.0.0.1:9090/")]
    [InlineData("kubernetes:///namespaces/cohesion/pods/orders-api:9090/")]
    [InlineData("docker:///networks/cohesion/containers/orders-api/")]
    [InlineData("docker:///networks/cohesion/containers/orders-api:0/")]
    [InlineData("docker:///engines/cohesion")]
    public void TryParse_WithForeignEndpoint_ShouldFail(string value)
    {
        // Arrange
        var endpoint = new Uri(value);

        // Act & Assert
        DockerProtocol.TryParseControlPlaneEndpoint(endpoint, out _, out _, out _).ShouldBeFalse();
        DockerProtocol.TryParseGatewayEndpoint(endpoint, out _).ShouldBeFalse();
    }
}
