using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using k8s;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesResourceApiTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Lists: Restore item apiVersion and kind from typed lists")]
    public async Task ListSupportedObjectsAsync_OnOmittedItemMetadata_ShouldRestoreAllTypes()
    {
        using var handler = new ListHandler();
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://kubernetes.test" }, [handler]);
        var items = await new KubernetesResourceApi(client, "appa@kubernetes")
            .ListSupportedObjectsAsync("appa", "worker", CancellationToken.None);
        items.Count.ShouldBe(8);
        foreach (var item in items)
        {
            item.Kind.ShouldNotBeNullOrWhiteSpace();
            item.ApiVersion.ShouldBe(item.Kind switch
            {
                "Deployment" or "StatefulSet" or "DaemonSet" => "apps/v1",
                "Job" => "batch/v1",
                _ => "v1",
            });
        }
    }

    private sealed class ListHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string collection = request.RequestUri!.AbsolutePath.Split('/')[^1];
            string kind = collection switch
            {
                "configmaps" => "ConfigMap", "secrets" => "Secret", "services" => "Service",
                "persistentvolumeclaims" => "PersistentVolumeClaim", "deployments" => "Deployment",
                "statefulsets" => "StatefulSet", "daemonsets" => "DaemonSet", "jobs" => "Job",
                _ => throw new InvalidOperationException(collection),
            };
            string api = kind switch
            {
                "Deployment" or "StatefulSet" or "DaemonSet" => "apps/v1",
                "Job" => "batch/v1", _ => "v1",
            };
            string json = $$$"""
                {"apiVersion":"{{{api}}}","kind":"{{{kind}}}List","items":[{"metadata":{"name":"worker","namespace":"appa"}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
