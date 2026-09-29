using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using k8s.Models;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesControlPlaneDiscoveryTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Discovery resync: Allocation, change, withdrawal, and transport retry flow through the gateway callback")]
    public async Task RefreshSystemDiscoveryAsync_OnLoadBalancerTransitions_ShouldPublishOnlyChangesAndRetryTransport()
    {
        var options = KubernetesSystemInstallationTests.Options(KubernetesSystemExposure.LoadBalancer);
        var warnings = new List<string>();
        options.WarningHandler = warnings.Add;
        var gateway = new KubernetesGateway(options);
        var model = KubernetesSystemInstallationTests.Model();
        using JsonDocument key = JsonDocument.Parse("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"public-x\",\"y\":\"public-y\"}");
        var document = ApplicationExportDocument.Create(model, "1", trustKey: key.RootElement);
        using var registration = new KubernetesGateway.NamespaceRegistration("appa", model.Owner, model) { LastExport = document };
        var api = new FakeKubernetesSystemApi();
        V1ConfigMap initial = KubernetesControlPlaneDiscovery.Create(document, "appa", model.Owner, null);
        await api.TryCreateAsync(initial, CancellationToken.None);
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        api.Applied.ShouldBeEmpty();
        V1Service service = new()
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta { Name = KubernetesSystemInstallation.PublicServiceName, NamespaceProperty = options.SystemNamespace },
            Status = new V1ServiceStatus { LoadBalancer = new V1LoadBalancerStatus { Ingress = [new V1LoadBalancerIngress { Ip = "192.0.2.10" }] } },
        };
        api.Objects[FakeKubernetesSystemApi.Key(service)] = service;
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        api.Applied.Count.ShouldBe(1);
        KubernetesControlPlaneDiscovery.ParseAddress((V1ConfigMap)api.Objects[FakeKubernetesSystemApi.Key(initial)]).Host.ShouldBe("192.0.2.10");
        service.Status.LoadBalancer.Ingress[0].Ip = "192.0.2.11";
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        api.Applied.Count.ShouldBe(2);
        service.Status.LoadBalancer.Ingress.Clear();
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        api.Applied.Count.ShouldBe(3);
        ((V1ConfigMap)api.Objects[FakeKubernetesSystemApi.Key(initial)]).Data.ShouldNotContainKey("control-plane.json");
        api.FailNextRead = true;
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        warnings.Count.ShouldBe(1);
        service.Status.LoadBalancer.Ingress.Add(new V1LoadBalancerIngress { Hostname = "restored.example.test" });
        await gateway.RefreshSystemDiscoveryAsync([registration], api, CancellationToken.None);
        api.Applied.Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Import: Reusing the resolver clears a withdrawn gateway address")]
    public async Task ResolveAsync_OnWithdrawnMetadata_ShouldClearCachedControlPlaneAddress()
    {
        var model = KubernetesSystemInstallationTests.Model();
        using JsonDocument key = JsonDocument.Parse("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"public-x\",\"y\":\"public-y\"}");
        var document = ApplicationExportDocument.Create(model, "1", trustKey: key.RootElement);
        V1ConfigMap export = KubernetesControlPlaneDiscovery.Create(document, "appa", model.Owner, new Uri("http://gateway.example.test:8080"));
        var resolver = new KubernetesApplicationModelResolver("appa", new(), _ => Task.FromResult(export));
        var context = new ApplicationModelResolutionContext(model.Environment, GatewayRunMode.Run, "remote");
        (await resolver.ResolveAsync(context, CancellationToken.None)).Name.ShouldBe(model.Name);
        resolver.ControlPlaneAddress.ShouldNotBeNull();
        export = KubernetesControlPlaneDiscovery.Create(document, "appa", model.Owner, null);
        await resolver.ResolveAsync(context, CancellationToken.None);
        resolver.ControlPlaneAddress.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Discovery: Publishes the upstream metadata shape for every exposure")]
    [InlineData(KubernetesSystemExposure.None, "http://cohesion-control-plane.cohesion-system.svc:8080")]
    [InlineData(KubernetesSystemExposure.LoadBalancer, "http://allocated.example.test:8080")]
    [InlineData(KubernetesSystemExposure.Ingress, "http://gateway.example.test:80")]
    public void Create_OnExposure_ShouldPublishImportableControlPlaneUrl(KubernetesSystemExposure exposure, string expected)
    {
        var options = KubernetesSystemInstallationTests.Options(exposure);
        using JsonDocument key = JsonDocument.Parse("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"public-x\",\"y\":\"public-y\"}");
        var model = KubernetesSystemInstallationTests.Model();
        var document = ApplicationExportDocument.Create(model, "1", trustKey: key.RootElement);
        var service = new V1Service { Status = new V1ServiceStatus { LoadBalancer = new V1LoadBalancerStatus { Ingress = [new V1LoadBalancerIngress { Hostname = "allocated.example.test" }] } } };
        Uri? address = KubernetesControlPlaneDiscovery.Address(options, service);
        var export = KubernetesControlPlaneDiscovery.Create(document, "appa", model.Owner, address);
        using JsonDocument metadata = JsonDocument.Parse(export.Data["control-plane.json"]);
        metadata.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["url", "trustKey"]);
        metadata.RootElement.GetProperty("url").GetString().ShouldBe(expected);
        metadata.RootElement.GetProperty("trustKey").GetRawText().ShouldBe(key.RootElement.GetRawText());
        KubernetesControlPlaneDiscovery.ParseAddress(export).ToEndpointString().ShouldBe(expected);
        ApplicationExportDocument.Parse(export.Data["export.json"]).Application.ShouldBe("appa");
        export.Data.Keys.ShouldNotContain("token");
        export.Data.Values.ShouldNotContain(value => value.Contains("bootstrapToken", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Discovery: Pending LoadBalancer allocation does not advertise a fictitious URL")]
    public void Address_OnPendingLoadBalancer_ShouldWaitForAllocation() =>
        KubernetesControlPlaneDiscovery.Address(KubernetesSystemInstallationTests.Options(KubernetesSystemExposure.LoadBalancer)).ShouldBeNull();

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Import: Exposes typed address discovery while preserving the common resolver")]
    public void ImportFromKubernetes_OnFactory_ShouldExposeCommonAndAddressContracts()
    {
        IKubernetesApplicationModelResolver resolver = KubernetesApplicationModelResolvers.ImportFromKubernetes("appa", new());
        resolver.ShouldBeAssignableTo<IApplicationModelResolver>();
        resolver.ControlPlaneAddress.ShouldBeNull();
    }
}
