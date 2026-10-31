using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesInfrastructureTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Kubernetes] - Infrastructure: ";

    [Fact(DisplayName = DisplayPrefix + "The infrastructure is the kubernetes platform in its namespace")]
    public async Task Create_WithKubeConfig_ShouldNameThePlatformAndNamespace()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();

        // Act
        await using KubernetesInfrastructure infrastructure = kubeConfig.Create("orders");

        // Assert
        infrastructure.Platform.ShouldBe((PlatformName)"kubernetes");
        infrastructure.Namespace.ShouldBe("orders");
    }

    [Theory(DisplayName = DisplayPrefix + "A namespace that is not a DNS label is refused")]
    [InlineData("")]
    [InlineData("Orders")]
    [InlineData("-orders")]
    [InlineData("orders_api")]
    public void Create_WithInvalidNamespace_ShouldThrow(string ns)
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();

        // Act & Assert
        Should.Throw<ArgumentException>(() => kubeConfig.Create(ns));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource is bound to one image")]
    public async Task AddImage_WithBoundResource_ShouldThrow()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        await using KubernetesInfrastructure infrastructure = kubeConfig.Create();
        infrastructure.AddImage("Web", "orders-api", "localhost:5001/web:e2e");

        // Act & Assert
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", "localhost:5001/web:e2e"));
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "storefront", " "));
    }

    [Fact(DisplayName = DisplayPrefix + "The instance client calls only Kubernetes control-plane endpoints")]
    public async Task ExecuteAsync_WithForeignControlPlane_ShouldThrow()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        await using KubernetesInfrastructure infrastructure = kubeConfig.Create();
        IApplicationInstanceClient client = infrastructure;
        var manifest = new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), null, new Uri("http://127.0.0.1:9090/"), []);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => client.ExecuteAsync(
            manifest,
            "greeting",
            ApplicationActionContext.Create(ApplicationActionKind.Read, JsonApplicationContentSerializer.Create())).AsTask());
    }
}
