using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal interface IKubernetesImageRegistryRoute
{
    Task<bool> IsKindAsync(CancellationToken cancellationToken);
    Task PushAsync(IContainerImageIndexEntry entry, string indexPath, string registry, CancellationToken cancellationToken);
}

internal sealed class KubernetesImageRegistryRoute(KubernetesGatewayOptions options) : IKubernetesImageRegistryRoute
{
    public Task<bool> IsKindAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? context = options.ContextName ?? KubernetesClientFactory.ResolveConfiguration(options).CurrentContext;
        return Task.FromResult(context?.StartsWith("kind-", StringComparison.Ordinal) == true);
    }

    public async Task PushAsync(IContainerImageIndexEntry entry, string indexPath, string registry, CancellationToken cancellationToken)
    {
        string storePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath))!, ".oci-store");
        IOciImageStore store = OciImageStores.Create(storePath);
        await store.IngestAsync(entry, indexPath, cancellationToken).ConfigureAwait(false);
        string scheme = registry.StartsWith("localhost:", StringComparison.Ordinal) ||
            registry.StartsWith("127.0.0.1:", StringComparison.Ordinal) ? "http" : "https";
        await OciRegistryPush.PushAsync(storePath, entry.Repository, entry.Digest,
            new Uri($"{scheme}://{registry}"), cancellationToken).ConfigureAwait(false);
        options.WarningHandler($"Pushed {registry}/{entry.Repository}@{entry.Digest}");
    }
}
