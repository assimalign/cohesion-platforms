using System;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesApplicationModelResolver : IKubernetesApplicationModelResolver
{
    private readonly ApplicationName _application;
    private readonly KubernetesGatewayOptions _options;
    private readonly Func<CancellationToken, Task<V1ConfigMap>> _readExport;

    public KubernetesApplicationModelResolver(
        ApplicationName application,
        KubernetesGatewayOptions options,
        Func<CancellationToken, Task<V1ConfigMap>>? readExport = null)
    {
        _application = application;
        _options = options;
        _readExport = readExport ?? ReadExportAsync;
    }

    public Uri? ControlPlaneAddress { get; private set; }

    public async ValueTask<Uri> ResolveControlPlaneAddressAsync(CancellationToken cancellationToken = default)
    {
        V1ConfigMap export = await _readExport(cancellationToken).ConfigureAwait(false);
        return ControlPlaneAddress = KubernetesControlPlaneDiscovery.ParseAddress(export);
    }

    public async ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        string namespaceName = KubernetesMetadata.NamespaceName(_application);
        V1ConfigMap export = await _readExport(cancellationToken).ConfigureAwait(false);
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

        if (export.Data.ContainsKey("control-plane.json"))
        {
            ControlPlaneAddress = KubernetesControlPlaneDiscovery.ParseAddress(export);
        }
        else
        {
            ControlPlaneAddress = null;
        }
        return document.ToModel(context.RunMode, context.GatewayIdentity);
    }

    private async Task<V1ConfigMap> ReadExportAsync(CancellationToken cancellationToken)
    {
        using IKubernetes client = KubernetesClientFactory.Create(_options);
        return await client.CoreV1.ReadNamespacedConfigMapAsync(KubernetesMetadata.ExportName,
            KubernetesMetadata.NamespaceName(_application), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
