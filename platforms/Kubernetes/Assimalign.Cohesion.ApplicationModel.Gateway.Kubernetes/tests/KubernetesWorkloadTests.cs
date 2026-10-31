using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using k8s.Models;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesWorkloadTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Kubernetes] - Workload: ";

    private static readonly KubernetesInfrastructureOptions _options = new();

    [Fact(DisplayName = DisplayPrefix + "A pod receives the runtime contract, its own pod, and its settings as its environment")]
    public void CreateDeployment_ShouldHandThePodTheRuntimeContract()
    {
        // Arrange
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();
        DeploymentId deployment = DeploymentId.New();

        // Act
        V1Deployment workload = KubernetesWorkload.CreateDeployment(model, resource, Settings(("Orders:Url", "http://orders-api.cohesion.svc.cluster.local")), deployment, "localhost:5001/web:e2e", "rev", "cohesion", _options);

        // Assert
        V1Container container = workload.Spec.Template.Spec.Containers.ShouldHaveSingleItem();
        Dictionary<string, V1EnvVar> environment = container.Env.ToDictionary(static variable => variable.Name);
        environment[AppEnvironment.Variables.Application].Value.ShouldBe("local");
        environment[AppEnvironment.Variables.Gateway].Value.ShouldBe("kubernetes");
        environment[AppEnvironment.Variables.Resource].Value.ShouldBe("orders-api");
        environment[AppEnvironment.Variables.ManifestEndpoint].Value.ShouldBe("kubernetes:///namespaces/cohesion");
        environment[AppEnvironment.Variables.Deployment].Value.ShouldBe(deployment.ToString());
        environment[AppEnvironment.Variables.Endpoint("http", "PORT")].Value.ShouldBe("8080");
        environment[AppEnvironment.Variables.Configuration("Orders:Url")].Value.ShouldBe("http://orders-api.cohesion.svc.cluster.local");
        environment["KUBERNETES_POD_NAME"].ValueFrom.FieldRef.FieldPath.ShouldBe("metadata.name");
        environment["KUBERNETES_POD_UID"].ValueFrom.FieldRef.FieldPath.ShouldBe("metadata.uid");
        container.Image.ShouldBe("localhost:5001/web:e2e");
        workload.Spec.Template.Spec.ServiceAccountName.ShouldBe(KubernetesWorkload.InstanceAccount);
        workload.Spec.Template.Spec.ShareProcessNamespace.ShouldBe(true);
        workload.Spec.Template.Metadata.Labels[KubernetesWorkload.DeploymentLabel].ShouldBe(deployment.ToString());
        workload.Spec.Replicas.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "WithReplicas sets how many instances run, on the kubernetes platform only")]
    public void GetReplicas_WithReplicas_ShouldReadIt()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("local");
        builder.AddResource("Web", "orders-api").WithReplicas(2).WithReplicas(3);

        // Act
        ApplicationResourceManifest resource = builder.Build().Manifest.Resources.Single();

        // Assert
        KubernetesWorkload.GetReplicas(resource).ShouldBe(3);
        resource.Extensions.ShouldHaveSingleItem().Platform.ShouldBe((PlatformName)"kubernetes");
    }

    [Fact(DisplayName = DisplayPrefix + "WithReplicas refuses fewer than one instance")]
    public void WithReplicas_WithZero_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDefinition resource = ApplicationModel.CreateBuilder("local").AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => resource.WithReplicas(0));
    }

    [Theory(DisplayName = DisplayPrefix + "A replicas extension that is not a whole number of at least 1 is refused")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("\"two\"")]
    public void GetReplicas_WithInvalidExtension_ShouldThrow(string replicas)
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource(replicas);

        // Act & Assert
        Should.Throw<ArgumentException>(() => KubernetesWorkload.GetReplicas(resource));
    }

    [Fact(DisplayName = DisplayPrefix + "The revision changes with what runs and with nothing else")]
    public void GetRevision_ShouldFollowImageAndSettings()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();

        // Act
        string first = KubernetesWorkload.GetRevision(resource, Settings(("A", "1"), ("B", "2")), "web:1", 8080, 9090);
        string reordered = KubernetesWorkload.GetRevision(resource, Settings(("B", "2"), ("A", "1")), "web:1", 8080, 9090);
        string changedSetting = KubernetesWorkload.GetRevision(resource, Settings(("A", "1"), ("B", "3")), "web:1", 8080, 9090);
        string changedImage = KubernetesWorkload.GetRevision(resource, Settings(("A", "1"), ("B", "2")), "web:2", 8080, 9090);

        // Assert
        reordered.ShouldBe(first);
        changedSetting.ShouldNotBe(first);
        changedImage.ShouldNotBe(first);
    }

    [Fact(DisplayName = DisplayPrefix + "The Service exposes the http endpoint on port 80 at the resource's cluster name")]
    public void CreateService_ShouldExposeTheEndpoint()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();

        // Act
        V1Service service = KubernetesWorkload.CreateService(resource, "cohesion", _options);

        // Assert
        V1ServicePort port = service.Spec.Ports.ShouldHaveSingleItem();
        port.Port.ShouldBe(80);
        port.TargetPort.Value.ShouldBe("8080");
        service.Spec.Selector[KubernetesWorkload.NameLabel].ShouldBe("orders-api");
        KubernetesWorkload.GetEndpoint(resource, "cohesion").ShouldBe("http://orders-api.cohesion.svc.cluster.local");
    }

    [Fact(DisplayName = DisplayPrefix + "An instance's service account may manage Leases and nothing else")]
    public void CreateInstanceAccess_ShouldGrantLeasesOnly()
    {
        // Act
        (_, V1Role role, V1RoleBinding binding) = KubernetesWorkload.CreateInstanceAccess("cohesion");

        // Assert
        V1PolicyRule rule = role.Rules.ShouldHaveSingleItem();
        rule.ApiGroups.ShouldBe(["coordination.k8s.io"]);
        rule.Resources.ShouldBe(["leases"]);
        binding.Subjects.ShouldHaveSingleItem().Name.ShouldBe(KubernetesWorkload.InstanceAccount);
    }

    private static (IApplicationModel Model, ApplicationResourceManifest Resource) CreateResource(string? replicas = null)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("local");
        IApplicationResourceDefinition definition = builder.AddResource("Web", "orders-api");
        if (replicas is not null)
        {
            using JsonDocument value = JsonDocument.Parse(replicas);
            definition.WithExtension("kubernetes", "replicas", value.RootElement.Clone());
        }

        IApplicationModel model = builder.Build();
        return (model, model.Manifest.Resources.Single());
    }

    private static Dictionary<SettingName, string> Settings(params (string Name, string Value)[] settings)
    {
        return settings.ToDictionary(static setting => (SettingName)setting.Name, static setting => setting.Value);
    }
}
