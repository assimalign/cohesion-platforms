using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Name: Should be the stable identity 'kubernetes'")]
    public void Name_OnGateway_ShouldBeKubernetes()
    {
        // Arrange
        var gateway = new KubernetesGateway();

        // Act
        string name = gateway.Name.Value;

        // Assert
        name.ShouldBe("kubernetes");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Ctor: Should throw ArgumentNullException for null options")]
    public void Ctor_OnNullOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        KubernetesGatewayOptions options = null!;

        // Act
        var exception = Should.Throw<ArgumentNullException>(() => new KubernetesGateway(options));

        // Assert
        exception.ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should build an application with the gateway selected")]
    public void UseKubernetesGateway_OnBuilderWithResource_ShouldBuildApplication()
    {
        // Arrange
        var builder = Application.CreateBuilder();
        builder.AddResource(new FakeExecutableResource("web"));

        // Act
        var application = builder.UseKubernetesGateway().Build();

        // Assert
        application.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should invoke the configure callback")]
    public void UseKubernetesGateway_OnConfigureOverload_ShouldInvokeConfigureCallback()
    {
        // Arrange
        var builder = Application.CreateBuilder();
        builder.AddResource(new FakeExecutableResource("web"));
        var invoked = false;

        // Act
        var application = builder
            .UseKubernetesGateway(options =>
            {
                invoked = true;
                options.FieldManager = "cohesion-test";
            })
            .Build();

        // Assert
        invoked.ShouldBeTrue();
        application.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should throw ArgumentNullException for null configure")]
    public void UseKubernetesGateway_OnNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        var builder = Application.CreateBuilder();

        // Act
        var exception = Should.Throw<ArgumentNullException>(() => builder.UseKubernetesGateway(null!));

        // Assert
        exception.ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default the server-side-apply field manager to 'cohesion-gateway'")]
    public void Options_OnDefaults_ShouldUseCohesionGatewayFieldManager()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.FieldManager.ShouldBe("cohesion-gateway");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default the readiness and stop budgets")]
    public void Options_OnDefaults_ShouldUseDefaultBudgets()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.ReadinessBudget.ShouldBe(TimeSpan.FromSeconds(60));
        options.StopGrace.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default kubeconfig path and context to null")]
    public void Options_OnDefaults_ShouldLeaveKubeConfigResolutionConventional()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.KubeConfigPath.ShouldBeNull();
        options.ContextName.ShouldBeNull();
    }
}
