using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Tests;

public class KubernetesProtocolTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Kubernetes] - Protocol: ";

    [Fact(DisplayName = DisplayPrefix + "A gateway endpoint names the namespace the gateway watches")]
    public void GatewayEndpoint_RoundTrip_ShouldKeepTheNamespace()
    {
        // Act
        Uri endpoint = KubernetesProtocol.CreateGatewayEndpoint("cohesion");
        bool parsed = KubernetesProtocol.TryParseGatewayEndpoint(endpoint, out string? ns);

        // Assert
        endpoint.AbsoluteUri.ShouldBe("kubernetes:///namespaces/cohesion");
        parsed.ShouldBeTrue();
        ns.ShouldBe("cohesion");
    }

    [Fact(DisplayName = DisplayPrefix + "A control-plane endpoint names the pod and port behind the API server's pod proxy")]
    public void ControlPlaneEndpoint_RoundTrip_ShouldKeepThePodAndPort()
    {
        // Act
        Uri endpoint = KubernetesProtocol.CreateControlPlaneEndpoint("cohesion", "orders-api-7d9f-xk2lp", 9090);
        bool parsed = KubernetesProtocol.TryParseControlPlaneEndpoint(endpoint, out string? ns, out string? pod, out int port);

        // Assert
        endpoint.AbsoluteUri.ShouldBe("kubernetes:///namespaces/cohesion/pods/orders-api-7d9f-xk2lp:9090/");
        parsed.ShouldBeTrue();
        ns.ShouldBe("cohesion");
        pod.ShouldBe("orders-api-7d9f-xk2lp");
        port.ShouldBe(9090);
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint of another form is not the platform's")]
    [InlineData("http://127.0.0.1:9090/")]
    [InlineData("inprocess://local/")]
    [InlineData("kubernetes:///namespaces/cohesion/pods/orders-api/")]
    [InlineData("kubernetes:///namespaces/cohesion/pods/orders-api:0/")]
    [InlineData("kubernetes:///clusters/cohesion")]
    public void TryParse_WithForeignEndpoint_ShouldFail(string value)
    {
        // Arrange
        var endpoint = new Uri(value);

        // Act & Assert
        KubernetesProtocol.TryParseControlPlaneEndpoint(endpoint, out _, out _, out _).ShouldBeFalse();
        KubernetesProtocol.TryParseGatewayEndpoint(endpoint, out _).ShouldBeFalse();
    }
}
