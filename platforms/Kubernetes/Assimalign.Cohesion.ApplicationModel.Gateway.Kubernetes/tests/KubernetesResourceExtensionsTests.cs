using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesResourceExtensionsTests
{
    private static readonly string Image = $"registry.example/test@sha256:{new string('a', 64)}";

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigureHpa: the bounds become neutral scale bounds and the targets Kubernetes data")]
    public void ConfigureHpa_ShouldDeclareNeutralBoundsAndTargets()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest()).ConfigureHpa(hpa =>
        {
            hpa.MinReplicas = 2;
            hpa.MaxReplicas = 6;
            hpa.TargetCpuUtilizationPercentage = 70;
        });

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];

        // Assert
        plan.Workload.Scale.ShouldBe(new ScaleBounds(2, 6));
        JsonElement targets = plan.Extensions["kubernetes/hpa"];
        targets.GetProperty("targetCpuUtilizationPercentage").GetInt32().ShouldBe(70);
        targets.TryGetProperty("minReplicas", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigureHpa: a later call starts from what earlier calls declared")]
    public void ConfigureHpa_Twice_ShouldCompose()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest())
            .ConfigureHpa(hpa =>
            {
                hpa.MaxReplicas = 4;
                hpa.TargetCpuUtilizationPercentage = 60;
            })
            .ConfigureHpa(hpa => hpa.TargetMemoryUtilizationPercentage = 75);

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];

        // Assert
        plan.Workload.Scale.ShouldBe(new ScaleBounds(1, 4));
        JsonElement targets = plan.Extensions["kubernetes/hpa"];
        targets.GetProperty("targetCpuUtilizationPercentage").GetInt32().ShouldBe(60);
        targets.GetProperty("targetMemoryUtilizationPercentage").GetInt32().ShouldBe(75);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigureHpa: a resource without bounds needs a maximum")]
    public void ConfigureHpa_WithoutMaximum_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddResource(CreateManifest());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => descriptor.ConfigureHpa(hpa => hpa.TargetCpuUtilizationPercentage = 70));

        // Assert
        exception.Message.ShouldContain("MaxReplicas", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigureHpa: a maximum above the manifest's maxReplicas fails Build")]
    public void Build_HpaAboveManifestCeiling_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        ResourceManifest manifest = CreateManifest();
        builder.AddResource(manifest with { Lifecycle = manifest.Lifecycle with { MaxReplicas = 3 } })
            .ConfigureHpa(hpa => hpa.MaxReplicas = 5);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("maxReplicas", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Extensions: a misspelt target in hand-written extension data fails Build")]
    public void Build_WithMalformedHpaExtension_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest())
            .ScaleBetween(1, 3)
            .SetExtension("kubernetes/hpa", Parse("""{"targetCpuUtilisationPercentage":70}"""));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("kubernetes/hpa", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Extensions: a kubernetes key the compiler does not know fails Build")]
    public void Build_WithUnsupportedKubernetesExtension_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest()).SetExtension("kubernetes/vpa", Parse("{}"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("kubernetes/vpa", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigurePodDisruptionBudget: the budget is Kubernetes extension data")]
    public void ConfigurePodDisruptionBudget_ShouldStoreTheBudget()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest()).ConfigurePodDisruptionBudget(budget => budget.MaxUnavailable = 1);

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];

        // Assert
        JsonElement budget = plan.Extensions["kubernetes/pod-disruption-budget"];
        budget.GetProperty("maxUnavailable").GetInt32().ShouldBe(1);
        budget.TryGetProperty("minAvailable", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - ConfigurePodDisruptionBudget: both counts at once are refused")]
    public void ConfigurePodDisruptionBudget_WithBothCounts_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddResource(CreateManifest());

        // Act
        Action configure = () => descriptor.ConfigurePodDisruptionBudget(budget =>
        {
            budget.MinAvailable = 1;
            budget.MaxUnavailable = 1;
        });

        // Assert
        configure.ShouldThrow<InvalidOperationException>();
    }

    private static IApplicationBuilder CreateBuilder() =>
        Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseKubernetesGateway(options => options.TrustKeyRepository = new TestGatewayTrustKeys());

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "web",
        Kind = "Web",
        Application = "appa",
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Assimalign.Cohesion.Test.Application",
            Image = Image,
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

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
