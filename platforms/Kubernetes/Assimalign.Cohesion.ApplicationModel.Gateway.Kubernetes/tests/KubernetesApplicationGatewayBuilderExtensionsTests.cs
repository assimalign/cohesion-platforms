using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesApplicationGatewayBuilderExtensionsTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Kubernetes] - UseKubernetes: ";

    private const string image = "localhost:5001/web:e2e";

    [Fact(DisplayName = DisplayPrefix + "The callback runs, and the infrastructure is created, only when the gateway is built")]
    public async Task UseKubernetes_WithCallback_ShouldCreateTheInfrastructureOnBuild()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        int calls = 0;
        KubernetesInfrastructureBuilder? captured = null;
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseKubernetes(kubernetes =>
        {
            calls++;
            captured = kubernetes;
            kubernetes.Options.KubeConfigPath = kubeConfig.Path;
            kubernetes.Options.Namespace = "shop";
            kubernetes.AddImage("Web", "orders-api", image);
        });
        builder.AddResource("Web", "orders-api");

        // Act
        int before = calls;
        await using ApplicationGateway gateway = builder.Build();

        // Assert
        before.ShouldBe(0);
        calls.ShouldBe(1);
        KubernetesInfrastructure infrastructure = captured.ShouldNotBeNull().Built.ShouldNotBeNull();
        infrastructure.Namespace.ShouldBe("shop");
        Should.Throw<ArgumentException>(() => infrastructure.AddImage("Web", "orders-api", image));
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway owns the infrastructure the callback builds: disposing the gateway disposes it")]
    public async Task UseKubernetes_WithCallback_ShouldBeDisposedWithTheGateway()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        KubernetesInfrastructureBuilder? captured = null;
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseKubernetes(kubernetes =>
        {
            captured = kubernetes;
            kubernetes.Options.KubeConfigPath = kubeConfig.Path;
        });
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.Build();
        KubernetesInfrastructure infrastructure = captured.ShouldNotBeNull().Built.ShouldNotBeNull();

        // Act
        await gateway.DisposeAsync();

        // Assert
        Should.Throw<ObjectDisposedException>(() => infrastructure.AddImage("Web", "orders-api", image));
    }

    [Fact(DisplayName = DisplayPrefix + "What the callback or the infrastructure's creation throws, the gateway's build throws")]
    public void UseKubernetes_WithInvalidConfiguration_ShouldFailTheBuild()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        ApplicationGatewayBuilder invalidNamespace = ApplicationGateway.CreateBuilder("shop").UseKubernetes(kubernetes =>
        {
            kubernetes.Options.KubeConfigPath = kubeConfig.Path;
            kubernetes.Options.Namespace = "Shop";
        });
        ApplicationGatewayBuilder duplicateImage = ApplicationGateway.CreateBuilder("shop").UseKubernetes(kubernetes => kubernetes
            .AddImage("Web", "orders-api", image)
            .AddImage("Web", "orders-api", image));

        // Act & Assert
        Should.Throw<ArgumentException>(invalidNamespace.Build);
        Should.Throw<ArgumentException>(duplicateImage.Build);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway borrows an infrastructure it is given: disposing the gateway leaves it to its caller")]
    public async Task UseKubernetes_WithInfrastructure_ShouldLeaveItUndisposed()
    {
        // Arrange
        using var kubeConfig = new TestKubeConfig();
        await using KubernetesInfrastructure infrastructure = kubeConfig.Create();
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop").UseKubernetes(infrastructure);
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.Build();

        // Act
        await gateway.DisposeAsync();

        // Assert
        infrastructure.AddImage("Web", "orders-api", image).ShouldBeSameAs(infrastructure);
    }

    [Fact(DisplayName = DisplayPrefix + "A null callback or infrastructure is refused")]
    public void UseKubernetes_WithNull_ShouldThrow()
    {
        // Arrange
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("shop");

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => builder.UseKubernetes((Action<KubernetesInfrastructureBuilder>)null!));
        Should.Throw<ArgumentNullException>(() => builder.UseKubernetes((KubernetesInfrastructure)null!));
    }
}
