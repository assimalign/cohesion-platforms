using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal static class KubernetesControlPlaneDiscovery
{
    internal static Uri? Address(KubernetesGatewayOptions options, V1Service? allocated = null)
    {
        string? host = options.SystemExposure switch
        {
            KubernetesSystemExposure.None => $"{KubernetesSystemInstallation.ControlPlaneName}.{options.SystemNamespace}.svc",
            KubernetesSystemExposure.Ingress => options.SystemIngressHost,
            KubernetesSystemExposure.LoadBalancer => LoadBalancerHost(allocated),
            _ => throw new ArgumentOutOfRangeException(nameof(options.SystemExposure)),
        };
        return host is null ? null : Uri.CreateEndpoint("http", host,
            options.SystemExposure == KubernetesSystemExposure.Ingress ? 80 : KubernetesSystemInstallation.ControlPort);
    }

    internal static V1ConfigMap Create(
        ApplicationExportDocument document, string namespaceName, string owner, Uri? address)
    {
        using var export = new MemoryStream();
        document.Save(export);
        var data = new Dictionary<string, string> { ["export.json"] = Encoding.UTF8.GetString(export.ToArray()) };
        if (address is not null && document.TrustKey is JsonElement trustKey)
        {
            using var metadata = new MemoryStream();
            using (var writer = new Utf8JsonWriter(metadata))
            {
                writer.WriteStartObject();
                writer.WriteString("url", address.ToEndpointString());
                writer.WritePropertyName("trustKey");
                trustKey.WriteTo(writer);
                writer.WriteEndObject();
            }
            data["control-plane.json"] = Encoding.UTF8.GetString(metadata.ToArray());
        }
        V1ObjectMeta objectMetadata = KubernetesMetadata.CreateObjectMeta(KubernetesMetadata.ExportName, namespaceName, "cohesion-gateway", "discovery/v1", owner);
        objectMetadata.Labels.Remove(KubernetesMetadata.ResourceLabel);
        objectMetadata.Labels[KubernetesMetadata.SystemLabel] = "gateway";
        return new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = objectMetadata,
            Data = data,
        };
    }

    internal static Uri ParseAddress(V1ConfigMap export)
    {
        if (export.Data is null || !export.Data.TryGetValue("control-plane.json", out string? json))
        {
            throw new InvalidDataException("Kubernetes export does not contain control-plane.json; external allocation may still be pending.");
        }

        using JsonDocument metadata = JsonDocument.Parse(json);
        if (!metadata.RootElement.TryGetProperty("url", out JsonElement url)
            || url.ValueKind != JsonValueKind.String || !Uri.TryParseEndpoint(url.GetString()!, out Uri? address)
            || !metadata.RootElement.TryGetProperty("trustKey", out JsonElement trustKey)
            || trustKey.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Kubernetes control-plane.json must contain a valid url and public trustKey.");
        }

        return address!;
    }

    private static string? LoadBalancerHost(V1Service? service)
    {
        if (service?.Status?.LoadBalancer?.Ingress is not { } addresses)
        {
            return null;
        }

        foreach (V1LoadBalancerIngress address in addresses)
        {
            if (!string.IsNullOrWhiteSpace(address.Hostname))
            {
                return address.Hostname;
            }

            if (!string.IsNullOrWhiteSpace(address.Ip))
            {
                return address.Ip;
            }
        }
        return null;
    }
}
