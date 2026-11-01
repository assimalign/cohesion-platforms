using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesInfrastructureBuilderTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Kubernetes] - Infrastructure builder: ";

    private const string image = "localhost:5001/web:e2e";

    [Fact(DisplayName = DisplayPrefix + "Build creates the infrastructure for the cluster and namespace its options name")]
    public async Task Build_WithOptions_ShouldCreateTheInfrastructure()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        var builder = new KubernetesInfrastructureBuilder();
        builder.Options.KubeConfigPath = kubeConfig.Path;
        builder.Options.Namespace = "orders";

        // Act
        await using KubernetesInfrastructure infrastructure = builder.Build();

        // Assert
        infrastructure.Platform.ShouldBe((PlatformName)"kubernetes");
        infrastructure.Namespace.ShouldBe("orders");
    }

    [Fact(DisplayName = DisplayPrefix + "Build copies the options: changing them later changes nothing")]
    public async Task Build_ThenChangedOptions_ShouldKeepTheBuiltOnes()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        var builder = new KubernetesInfrastructureBuilder();
        builder.Options.KubeConfigPath = kubeConfig.Path;
        builder.Options.Namespace = "orders";
        await using KubernetesInfrastructure infrastructure = builder.Build();

        // Act
        builder.Options.Namespace = "storefront";

        // Assert
        infrastructure.Namespace.ShouldBe("orders");
    }

    [Theory(DisplayName = DisplayPrefix + "Build refuses a namespace that is not a DNS label, or a port out of range")]
    [InlineData("Orders", 8080, 9090)]
    [InlineData("-orders", 8080, 9090)]
    [InlineData("orders", 0, 9090)]
    [InlineData("orders", 8080, 65536)]
    public void Build_WithInvalidOptions_ShouldThrow(string ns, int endpointPort, int controlPlanePort)
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        var builder = new KubernetesInfrastructureBuilder();
        builder.Options.KubeConfigPath = kubeConfig.Path;
        builder.Options.Namespace = ns;
        builder.Options.EndpointPort = endpointPort;
        builder.Options.ControlPlanePort = controlPlanePort;

        // Act & Assert
        Should.Throw<ArgumentException>(builder.Build);
    }

    [Fact(DisplayName = DisplayPrefix + "AddImage refuses a resource that already has an image, an empty image, or a missing name, when it is called")]
    public void AddImage_WithInvalidBinding_ShouldThrow()
    {
        // Arrange
        var builder = new KubernetesInfrastructureBuilder().AddImage("Web", "orders-api", image);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", "orders-api", "localhost:5001/other:e2e"));
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", "storefront", " "));
        Should.Throw<ArgumentException>(() => builder.AddImage(default, "storefront", image));
        Should.Throw<ArgumentException>(() => builder.AddImage("Web", default, image));
    }

    [Fact(DisplayName = DisplayPrefix + "Build binds every image it was given to the infrastructure")]
    public async Task Build_WithImages_ShouldBindThem()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        var builder = new KubernetesInfrastructureBuilder()
            .AddImage("Web", "orders-api", image)
            .AddImage("Web", "storefront", image);
        builder.Options.KubeConfigPath = kubeConfig.Path;

        // Act
        await using KubernetesInfrastructure infrastructure = builder.Build();

        // Assert: a bound resource takes no second image.
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", image));
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "storefront", image));
        infrastructure.AddImage("Web", "catalog", image).ShouldBeSameAs(infrastructure);
    }

    [Fact(DisplayName = DisplayPrefix + "Each Build creates an infrastructure of its own")]
    public async Task Build_Twice_ShouldCreateTwoInfrastructures()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        var builder = new KubernetesInfrastructureBuilder();
        builder.Options.KubeConfigPath = kubeConfig.Path;

        // Act
        await using KubernetesInfrastructure first = builder.Build();
        await using KubernetesInfrastructure second = builder.Build();

        // Assert
        second.ShouldNotBeSameAs(first);
    }
}
