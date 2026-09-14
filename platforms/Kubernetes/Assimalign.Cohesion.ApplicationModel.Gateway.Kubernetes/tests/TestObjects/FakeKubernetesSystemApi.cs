using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

internal sealed class FakeKubernetesSystemApi : IKubernetesResourceApi
{
    internal Dictionary<string, IKubernetesObject<V1ObjectMeta>> Objects { get; } = new(StringComparer.Ordinal);
    internal List<IKubernetesObject<V1ObjectMeta>> Applied { get; } = [];
    internal int Reads { get; private set; }
    internal int Deletes { get; private set; }
    internal Action<IKubernetesObject<V1ObjectMeta>>? BeforeCreate { get; set; }
    internal Action<IKubernetesObject<V1ObjectMeta>>? BeforeApply { get; set; }
    internal bool FailNextRead { get; set; }
    private int _revision;
    internal static string Key(IKubernetesObject<V1ObjectMeta> value) => $"{value.Kind}/{value.Metadata.NamespaceProperty}/{value.Metadata.Name}";

    public Task<bool> TryCreateAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeCreate?.Invoke(resource);
        if (Objects.ContainsKey(Key(resource)))
        {
            return Task.FromResult(false);
        }

        IKubernetesObject<V1ObjectMeta> stored = Clone(resource);
        stored.Metadata.ResourceVersion = (++_revision).ToString();
        Objects.Add(Key(resource), stored);
        return Task.FromResult(true);
    }
    public Task ApplyAsync(IKubernetesObject<V1ObjectMeta> resource, bool force, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeApply?.Invoke(resource);
        if (Objects.TryGetValue(Key(resource), out var existing)
            && resource.Metadata.ResourceVersion != existing.Metadata.ResourceVersion)
        {
            throw new InvalidOperationException("The optimistic resourceVersion precondition failed.");
        }

        Applied.Add(resource);
        IKubernetesObject<V1ObjectMeta> stored = Clone(resource);
        stored.Metadata.ResourceVersion = (++_revision).ToString();
        Objects[Key(resource)] = stored;
        return Task.CompletedTask;
    }
    public Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        if (FailNextRead) { FailNextRead = false; throw new HttpRequestException("test transport interruption"); }
        return Task.FromResult(Objects.TryGetValue(Key(resource), out var value) ? Clone(value) : null);
    }
    public Task DeleteAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
    {
        Deletes++;
        Objects.Remove(Key(resource));
        return Task.CompletedTask;
    }
    public Task JsonPatchAsync(IKubernetesObject<V1ObjectMeta> resource, string patch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(string namespaceName, string? resourceLabel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>>(Objects.Values.Where(value => value.Metadata.NamespaceProperty == namespaceName).Select(Clone).ToArray());
    public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(string namespaceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<V1PodList> ListPodsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<V1Endpoints?> ReadEndpointsAsync(string namespaceName, string serviceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<V1Pod> WatchPodsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private static IKubernetesObject<V1ObjectMeta> Clone(IKubernetesObject<V1ObjectMeta> value)
    {
        string json = KubernetesJson.Serialize(value);
        return value switch
        {
            V1Namespace => KubernetesJson.Deserialize<V1Namespace>(json),
            V1ServiceAccount => KubernetesJson.Deserialize<V1ServiceAccount>(json),
            V1Role => KubernetesJson.Deserialize<V1Role>(json),
            V1RoleBinding => KubernetesJson.Deserialize<V1RoleBinding>(json),
            V1ClusterRole => KubernetesJson.Deserialize<V1ClusterRole>(json),
            V1ClusterRoleBinding => KubernetesJson.Deserialize<V1ClusterRoleBinding>(json),
            V1Secret => KubernetesJson.Deserialize<V1Secret>(json),
            V1ConfigMap => KubernetesJson.Deserialize<V1ConfigMap>(json),
            V1Service => KubernetesJson.Deserialize<V1Service>(json),
            V1PersistentVolumeClaim => KubernetesJson.Deserialize<V1PersistentVolumeClaim>(json),
            V1Deployment => KubernetesJson.Deserialize<V1Deployment>(json),
            V1Ingress => KubernetesJson.Deserialize<V1Ingress>(json),
            _ => throw new NotSupportedException(),
        };
    }
}
