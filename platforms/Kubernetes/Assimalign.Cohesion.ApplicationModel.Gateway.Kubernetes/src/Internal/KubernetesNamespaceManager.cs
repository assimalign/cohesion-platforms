using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Manages the application namespace: idempotent ensure via server-side apply, and
/// best-effort delete tolerating an already-absent namespace.
/// </summary>
internal sealed class KubernetesNamespaceManager
{
    private const string managedByLabel = "app.kubernetes.io/managed-by";
    private const string managedByValue = "cohesion";

    private readonly IKubernetes _client;
    private readonly string _fieldManager;

    public KubernetesNamespaceManager(IKubernetes client, string fieldManager)
    {
        _client = client;
        _fieldManager = fieldManager;
    }

    /// <summary>
    /// Ensures the namespace exists and carries the cohesion managed-by label, via
    /// server-side apply under the configured field manager. Idempotent.
    /// </summary>
    /// <param name="namespaceName">The RFC 1123 namespace name.</param>
    /// <param name="cancellationToken">Signals that the operation should be abandoned.</param>
    /// <returns>A task that completes once the namespace exists.</returns>
    public async Task EnsureAsync(string namespaceName, CancellationToken cancellationToken = default)
    {
        var body = new V1Namespace
        {
            ApiVersion = V1Namespace.KubeApiVersion,
            Kind = V1Namespace.KubeKind,
            Metadata = new V1ObjectMeta
            {
                Name = namespaceName,
                Labels = new Dictionary<string, string> { [managedByLabel] = managedByValue },
            },
        };

        var patch = new V1Patch(KubernetesJson.Serialize(body), V1Patch.PatchType.ApplyPatch);
        try
        {
            await _client.CoreV1
                .PatchNamespaceAsync(patch, namespaceName, fieldManager: _fieldManager, force: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Server-side apply creates missing objects on conformant API servers; this
            // fallback covers servers that reject apply for an absent cluster-scoped object.
            await _client.CoreV1
                .CreateNamespaceAsync(body, fieldManager: _fieldManager, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the namespace, silently tolerating a namespace that no longer exists.
    /// </summary>
    /// <param name="namespaceName">The RFC 1123 namespace name.</param>
    /// <param name="cancellationToken">Bounds how long deletion may take.</param>
    /// <returns>A task that completes once deletion has been requested.</returns>
    public async Task DeleteAsync(string namespaceName, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.CoreV1
                .DeleteNamespaceAsync(namespaceName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — deletion is idempotent by contract.
        }
    }
}
