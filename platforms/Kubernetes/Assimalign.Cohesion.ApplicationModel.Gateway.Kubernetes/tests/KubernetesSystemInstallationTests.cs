using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesSystemInstallationTests
{
    internal const string Image = "example/gateway@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Bootstrap: Late foreign object refuses the entire installation before mutation")]
    public async Task BootstrapAsync_OnForeignDeployment_ShouldPreflightBeforeAnyWrite()
    {
        var options = Options();
        var api = new FakeKubernetesSystemApi();
        V1Deployment foreign = KubernetesSystemInstallation.Create(options, [Model()]).OfType<V1Deployment>().Single();
        foreign.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation] = "foreign";
        api.Objects[FakeKubernetesSystemApi.Key(foreign)] = foreign;
        using var output = new StringWriter();
        await Should.ThrowAsync<InvalidOperationException>(() => new KubernetesGateway(options).BootstrapAsync([Model()], output, api));
        api.Applied.ShouldBeEmpty();
        api.Objects.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Bootstrap: Existing trust bytes survive guarded apply and create races fail closed")]
    public async Task BootstrapAsync_OnExistingSecretOrCreateRace_ShouldPreserveKeysAndRefuseRace()
    {
        var options = Options();
        var api = new FakeKubernetesSystemApi();
        V1Secret secret = KubernetesSystemInstallation.Create(options, [Model()]).OfType<V1Secret>().Single();
        secret.Data = new Dictionary<string, byte[]> { ["appa.kubernetes.p8"] = [1, 2, 3] };
        await api.TryCreateAsync(secret);
        using var output = new StringWriter();
        var gateway = new KubernetesGateway(options);
        await gateway.BootstrapAsync([Model()], output, api);
        ((V1Secret)api.Objects[FakeKubernetesSystemApi.Key(secret)]).Data["appa.kubernetes.p8"].ShouldBe(new byte[] { 1, 2, 3 });
        var racing = new FakeKubernetesSystemApi();
        racing.BeforeCreate = desired => racing.Objects[FakeKubernetesSystemApi.Key(desired)] = new V1Namespace
        {
            Kind = desired.Kind,
            Metadata = new V1ObjectMeta { Name = desired.Metadata.Name, Annotations = new Dictionary<string, string> { [KubernetesMetadata.OwnerAnnotation] = "foreign" } },
        };
        await Should.ThrowAsync<InvalidOperationException>(() => gateway.BootstrapAsync([Model()], output, racing));
        racing.Applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - System: Same-namespace infrastructure survives the application prune sweep")]
    public async Task PruneRemovedResourcesAsync_OnSystemObjects_ShouldPreserveInfrastructure()
    {
        var options = Options();
        options.SystemNamespace = "appa";
        IApplicationModel model = Model();
        var objects = KubernetesSystemInstallation.Create(options, [model]);
        objects.OfType<V1Namespace>().Single().Metadata.Annotations[KubernetesMetadata.OwnerAnnotation].ShouldBe(model.Owner);
        objects.OfType<V1ClusterRole>().Single().Rules.ShouldContain(rule => rule.Resources.Contains("namespaces") && rule.ResourceNames.Contains("appa"));
        var api = new FakeKubernetesSystemApi();
        foreach (var resource in objects)
        {
            resource.Metadata.Labels.ShouldNotContainKey(KubernetesMetadata.ResourceLabel);
            api.Objects.Add(FakeKubernetesSystemApi.Key(resource), resource);
        }
        var observations = new KubernetesGatewayObservationRegistry(api, _ => { });
        var controller = new KubernetesPlanController(options, new KubernetesPlanCompiler(), api, observations);
        await controller.PruneRemovedResourcesAsync(model, "appa", CancellationToken.None);
        api.Deletes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - System: Multi-model render emits application namespaces while apply rejects unresolved routing")]
    public async Task RenderAsync_OnMultipleModels_ShouldRenderOfflineButRefuseBootstrapApply()
    {
        var options = Options();
        var gateway = new KubernetesGateway(options);
        IApplicationModel[] models = [Model(), Model(applicationName: "appb")];
        using var output = new StringWriter();
        await gateway.RenderAsync(models, output, CancellationToken.None);
        output.ToString().ShouldContain("appb", Case.Sensitive);
        var api = new FakeKubernetesSystemApi();
        await Should.ThrowAsync<InvalidOperationException>(() => gateway.BootstrapAsync(models, output, api));
        api.Reads.ShouldBe(0);
        api.Applied.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - System: Each exposure retains owned system resources")]
    [InlineData(KubernetesSystemExposure.None, 10)]
    [InlineData(KubernetesSystemExposure.LoadBalancer, 11)]
    [InlineData(KubernetesSystemExposure.Ingress, 11)]
    public void Render_OnExposure_ShouldMatchSystemGolden(KubernetesSystemExposure exposure, int count)
    {
        KubernetesGatewayOptions options = Options(exposure);
        var objects = KubernetesSystemInstallation.Create(options, [Model()]);
        objects.Count.ShouldBe(count);
        objects[0].ShouldBeOfType<V1Namespace>();
        foreach (var resource in objects)
        {
            resource.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation].ShouldBe(options.FieldManager);
            resource.Metadata.Labels[KubernetesMetadata.ManagedByLabel].ShouldBe("cohesion");
        }
        V1Deployment deployment = objects.OfType<V1Deployment>().Single();
        deployment.Spec.Replicas.ShouldBe(1);
        deployment.Spec.Template.Spec.Containers.Single().Image.ShouldBe(Image);
        deployment.Spec.Template.Spec.Volumes.ShouldContain(volume => volume.PersistentVolumeClaim != null);
        deployment.Spec.Template.Spec.Volumes.ShouldContain(volume => volume.Secret != null);
        objects.OfType<V1Secret>().Single().Data.ShouldBeNull();
        objects.OfType<V1Service>().Single(service => service.Metadata.Name == "cohesion-control-plane").Spec.Type.ShouldBe("ClusterIP");
        if (exposure == KubernetesSystemExposure.LoadBalancer)
        {
            objects.OfType<V1Service>().Count(service => service.Spec.Type == "LoadBalancer").ShouldBe(1);
        }

        if (exposure == KubernetesSystemExposure.Ingress)
        {
            objects.OfType<V1Ingress>().Single().Spec.Rules.Single().Host.ShouldBe("gateway.example.test");
        }

        string shape = string.Join("\n", objects.Select(resource => $"{resource.Kind}/{resource.Metadata.Name}")) + "\n";
        shape.ShouldBe(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "system", exposure + ".txt")).Replace("\r\n", "\n"));
        RenderGolden.Verify(KubernetesPlanRenderer.Render(objects), Path.Combine("system", exposure + ".yaml"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - System: Render and bootstrap require explicit image and persistent size")]
    public void Create_OnMissingImageOrStorage_ShouldRejectNamedOption()
    {
        Should.Throw<ArgumentException>(() => KubernetesSystemInstallation.Create(new(), [])).ParamName.ShouldBe("SystemImage");
        Should.Throw<ArgumentException>(() => KubernetesSystemInstallation.Create(new() { SystemImage = Image }, [])).ParamName.ShouldBe("SystemStorageSize");
        Should.Throw<ArgumentException>(() => new KubernetesGateway(new() { SystemImage = "example/gateway:latest" }));
        new KubernetesGateway(new()).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Bootstrap: Always emits; applies only when configured")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BootstrapAsync_OnApplyMode_ShouldEmitAndHonorApply(bool apply)
    {
        var options = Options();
        options.BootstrapApply = apply;
        var gateway = new KubernetesGateway(options);
        var api = new FakeKubernetesSystemApi();
        using var output = new StringWriter();
        await gateway.BootstrapAsync([Model()], output, api, CancellationToken.None);
        output.ToString().ShouldContain("cohesion-control-plane", Case.Sensitive);
        api.Applied.Count.ShouldBe(apply ? 10 : 0);
        api.Reads.ShouldBe(apply ? 20 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Bootstrap: Foreign ownership requires explicit adoption")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapAsync_OnForeignOwner_ShouldRequireAdopt(bool adopt)
    {
        var options = Options();
        var gateway = new KubernetesGateway(options);
        var api = new FakeKubernetesSystemApi();
        var foreign = KubernetesSystemInstallation.Create(options, [Model()])[0];
        foreign.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation] = "foreign";
        api.Objects.Add(FakeKubernetesSystemApi.Key(foreign), foreign);
        using var output = new StringWriter();
        if (adopt)
        {
            await gateway.BootstrapAsync([Model(true)], output, api, CancellationToken.None);
        }
        else
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => gateway.BootstrapAsync([Model()], output, api, CancellationToken.None))).Message.ShouldContain("--adopt");
        }

        output.ToString().ShouldContain("Namespace", Case.Sensitive);
        api.Applied.Count.ShouldBe(adopt ? 10 : 0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Render: Composite resource plans remain in declaration order offline")]
    public async Task RenderAsync_OnCompositeModel_ShouldEmitSystemThenEveryResourceOffline()
    {
        var options = Options();
        options.KubeConfigPath = "does-not-exist";
        options.ImageRealizer = new RejectGather();
        var gateway = new KubernetesGateway(options);
        IApplicationModel model = Model(resources: ["first", "second"]);
        using var output = new StringWriter();
        await gateway.RenderAsync([model], output, CancellationToken.None);
        var documents = output.ToString().Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        documents[0].ShouldContain("Namespace", Case.Sensitive);
        string text = output.ToString();
        text.IndexOf("cohesion-control-plane", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("first", StringComparison.Ordinal));
        text.IndexOf("first", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("second", StringComparison.Ordinal));
        RenderGolden.Verify(text, Path.Combine("system", "composite.yaml"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Render: Application preview needs no system image or storage")]
    public async Task RenderAsync_OnApplicationOnlyModel_ShouldOmitSystemInstallation()
    {
        using var output = new StringWriter();
        await new KubernetesGateway(new KubernetesGatewayOptions { KubeConfigPath = "missing-kubeconfig" })
            .RenderAsync([Model()], output, CancellationToken.None);
        string rendered = output.ToString();
        rendered.ShouldContain("Deployment", Case.Sensitive);
        rendered.ShouldContain("web", Case.Sensitive);
        rendered.ShouldNotContain("cohesion-gateway-state", Case.Sensitive);
        rendered.ShouldNotContain("ClusterRole", Case.Sensitive);
    }

    internal static KubernetesGatewayOptions Options(KubernetesSystemExposure exposure = KubernetesSystemExposure.None) => new()
    {
        SystemImage = Image,
        SystemStorageSize = "1Gi",
        SystemExposure = exposure,
        SystemIngressHost = "gateway.example.test",
        SystemIngressClass = "nginx",
    };

    internal static IApplicationModel Model(bool adopt = false, string[]? resources = null, string applicationName = "appa")
    {
        var builder = Application.CreateBuilder(ApplicationName.Parse(applicationName), adopt ? ["--adopt"] : []);
        foreach (string resource in resources ?? ["web"])
        {
            builder.AddResource(new ResourceManifest
            {
                Name = resource,
                Application = applicationName,
                Kind = "Web",
                ApplicationModel = "Example.ApplicationModel",
                Artifact = new ResourceManifestArtifact { Image = Image, Assembly = "Example.Web" },
                Endpoints = [new ResourceManifestEndpoint { Name = "http", Protocol = "tcp", Scheme = "http", ContainerPort = 8080 }],
                ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
            });
        }
        return builder.UseKubernetesGateway().Build().Model;
    }

    private sealed class RejectGather : IImageRealizer
    {
        public Task<IContainerImageArtifact> RealizeAsync(ResourceId resource, string imageReference, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Render must never gather.");
    }
}
