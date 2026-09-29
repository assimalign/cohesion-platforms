using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal interface IKubernetesResourceApi
{
    Task<bool> TryCreateAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default);

    Task ApplyAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        bool force,
        CancellationToken cancellationToken = default);

    Task JsonPatchAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        string patch,
        CancellationToken cancellationToken = default);

    Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
        string namespaceName,
        string? resourceLabel,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        string namespaceName,
        CancellationToken cancellationToken = default);

    Task<V1PodList> ListPodsAsync(
        string namespaceName,
        string? labelSelector = null,
        CancellationToken cancellationToken = default);

    Task<V1Endpoints?> ReadEndpointsAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<V1Pod> WatchPodsAsync(
        CancellationToken cancellationToken = default);
}
