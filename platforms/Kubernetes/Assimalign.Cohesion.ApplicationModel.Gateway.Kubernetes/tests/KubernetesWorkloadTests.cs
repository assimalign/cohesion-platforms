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
        V1Deployment workload = KubernetesWorkload.CreateDeployment(model, resource, Settings(("Orders:Url", "http://orders-api.cohesion.svc.cluster.local")), deployment, "localhost:5001/web:e2e", "rev", "work", "cohesion", _options);

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

    [Fact(DisplayName = DisplayPrefix + "A pod is ready once its program accepts connections on its http endpoint")]
    public void CreateDeployment_ShouldProbeReadinessOnTheHttpEndpoint()
    {
        // Arrange
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();

        // Act
        V1Deployment workload = KubernetesWorkload.CreateDeployment(model, resource, Settings(), DeploymentId.New(), "web:1", "rev", "work", "cohesion", _options);

        // Assert
        V1Probe probe = workload.Spec.Template.Spec.Containers.ShouldHaveSingleItem().ReadinessProbe.ShouldNotBeNull();
        probe.TcpSocket.ShouldNotBeNull().Port.Value.ShouldBe("http");
        probe.PeriodSeconds.ShouldBe(2);
        probe.HttpGet.ShouldBeNull();
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

    [Fact(DisplayName = DisplayPrefix + "The revision follows the image and ports, and the workload follows the revision, replicas, and settings")]
    public void GetRevisionAndWorkload_ShouldFollowWhatRuns()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();
        (_, ApplicationResourceManifest replicated) = CreateResource("3");
        string revision = KubernetesWorkload.GetRevision("web:1", 8080, 9090);

        // Act
        string first = KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "2")), "IfNotPresent", null);
        string reordered = KubernetesWorkload.GetWorkload(revision, resource, Settings(("B", "2"), ("A", "1")), "IfNotPresent", null);
        string changedSetting = KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "3")), "IfNotPresent", null);
        string changedReplicas = KubernetesWorkload.GetWorkload(revision, replicated, Settings(("A", "1"), ("B", "2")), "IfNotPresent", null);
        string changedImage = KubernetesWorkload.GetWorkload(KubernetesWorkload.GetRevision("web:2", 8080, 9090), resource, Settings(("A", "1"), ("B", "2")), "IfNotPresent", null);

        // Assert
        KubernetesWorkload.GetRevision("web:1", 8080, 9090).ShouldBe(revision);
        KubernetesWorkload.GetRevision("web:1", 8081, 9090).ShouldNotBe(revision);
        KubernetesWorkload.GetRevision("web:1", 8080, 9091).ShouldNotBe(revision);
        reordered.ShouldBe(first);
        changedSetting.ShouldNotBe(first);
        changedReplicas.ShouldNotBe(first);
        changedImage.ShouldNotBe(first);
        KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "2")), "Always", null).ShouldNotBe(first);
        KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "1"), ("B", "2")), "IfNotPresent", "Staging").ShouldNotBe(first);
    }

    [Fact(DisplayName = DisplayPrefix + "Settings whose values contain the separators never hash like another set of settings")]
    public void GetWorkload_WithSeparatorsInValues_ShouldNotCollide()
    {
        // Arrange
        (_, ApplicationResourceManifest resource) = CreateResource();
        string revision = KubernetesWorkload.GetRevision("web:1", 8080, 9090);

        // Act: without length prefixes, both read "A=x\nB=y\n".
        string one = KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "x\nB=y")), "IfNotPresent", null);
        string two = KubernetesWorkload.GetWorkload(revision, resource, Settings(("A", "x"), ("B", "y")), "IfNotPresent", null);

        // Assert
        one.ShouldNotBe(two);
    }

    [Fact(DisplayName = DisplayPrefix + "The Deployment carries its revision and workload, and its pods the revision only")]
    public void CreateDeployment_ShouldAnnotateRevisionAndWorkload()
    {
        // Arrange
        (IApplicationModel model, ApplicationResourceManifest resource) = CreateResource();

        // Act
        V1Deployment workload = KubernetesWorkload.CreateDeployment(model, resource, Settings(), DeploymentId.New(), "web:1", "rev", "work", "cohesion", _options);

        // Assert
        workload.Metadata.Annotations[KubernetesWorkload.RevisionAnnotation].ShouldBe("rev");
        workload.Metadata.Annotations[KubernetesWorkload.WorkloadAnnotation].ShouldBe("work");
        workload.Spec.Template.Metadata.Annotations[KubernetesWorkload.RevisionAnnotation].ShouldBe("rev");
        workload.Spec.Template.Metadata.Annotations.ShouldNotContainKey(KubernetesWorkload.WorkloadAnnotation);
    }

    [Fact(DisplayName = DisplayPrefix + "The workload decides the plan: none running is a create, the same is nothing, any other is an update")]
    public void GetChange_ShouldCompareTheWorkload()
    {
        // Act & Assert
        KubernetesWorkload.GetChange(null, "work").ShouldBe(ApplicationGatewayDeploymentChange.Create);
        KubernetesWorkload.GetChange(Running(new() { [KubernetesWorkload.WorkloadAnnotation] = "work" }), "work").ShouldBe(ApplicationGatewayDeploymentChange.None);
        KubernetesWorkload.GetChange(Running(new() { [KubernetesWorkload.WorkloadAnnotation] = "work" }, replicas: 2, observed: 3, generation: 3, updated: 2, available: 2, total: 2), "work").ShouldBe(ApplicationGatewayDeploymentChange.None);
        KubernetesWorkload.GetChange(Running(new() { [KubernetesWorkload.WorkloadAnnotation] = "other" }), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);

        // A Deployment an earlier template version created carries a revision and no workload.
        KubernetesWorkload.GetChange(Running(new() { [KubernetesWorkload.RevisionAnnotation] = "work" }), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);
        KubernetesWorkload.GetChange(Running(null), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);
    }

    [Fact(DisplayName = DisplayPrefix + "A Deployment of the workload whose rollout has not finished, or failed, is an update, not nothing")]
    public void GetChange_WithUnfinishedRollout_ShouldUpdate()
    {
        // Arrange
        Dictionary<string, string> annotations = new() { [KubernetesWorkload.WorkloadAnnotation] = "work" };

        // Act & Assert: the controller has not seen the latest spec.
        KubernetesWorkload.GetChange(Running(annotations, replicas: 2, observed: 2, generation: 3, updated: 2, available: 2, total: 2), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);

        // A new pod in CrashLoopBackOff: updated, but not available.
        KubernetesWorkload.GetChange(Running(annotations, replicas: 2, observed: 3, generation: 3, updated: 2, available: 1, total: 2), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);

        // The rollout stalled: an old pod remains, and not every replica runs the new template.
        KubernetesWorkload.GetChange(Running(annotations, replicas: 2, observed: 3, generation: 3, updated: 1, available: 2, total: 3), "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);

        // No status at all.
        KubernetesWorkload.GetChange(new V1Deployment { Metadata = new V1ObjectMeta { Name = "orders-api", Annotations = annotations } }, "work").ShouldBe(ApplicationGatewayDeploymentChange.Update);
    }

    // A Deployment as the API server reports it; by default rolled out with one replica.
    private static V1Deployment Running(Dictionary<string, string>? annotations, int replicas = 1, long observed = 1, long generation = 1, int updated = 1, int available = 1, int total = 1)
    {
        return new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "orders-api", Annotations = annotations, Generation = generation },
            Spec = new V1DeploymentSpec { Replicas = replicas },
            Status = new V1DeploymentStatus { ObservedGeneration = observed, UpdatedReplicas = updated, AvailableReplicas = available, Replicas = total },
        };
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
