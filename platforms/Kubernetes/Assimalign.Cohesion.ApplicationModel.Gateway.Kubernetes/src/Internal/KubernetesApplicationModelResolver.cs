using System;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesApplicationModelResolver : IApplicationModelResolver
{
    private readonly ApplicationName _application;
    private readonly KubernetesGatewayOptions _options;

    public KubernetesApplicationModelResolver(
        ApplicationName application,
        KubernetesGatewayOptions options)
    {
        _application = application;
        _options = options;
    }

    public async ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using IKubernetes client = KubernetesClientFactory.Create(_options);
        string namespaceName = KubernetesMetadata.NamespaceName(_application);
        V1ConfigMap export = await client.CoreV1.ReadNamespacedConfigMapAsync(
            KubernetesMetadata.ExportName,
            namespaceName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (export.Data is null
            || !export.Data.TryGetValue("export.json", out string? json)
            || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"ConfigMap '{namespaceName}/{KubernetesMetadata.ExportName}' does not contain export.json.");
        }

        ApplicationExportDocument document = ApplicationExportDocument.Parse(json);
        if (!string.Equals(document.Application, _application.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes export identifies application '{document.Application}', not '{_application}'.");
        }

        return document.ToModel(context.RunMode, context.GatewayIdentity);
    }
}
