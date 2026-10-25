using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// A ConfigMap-only cluster for the record store: server-side apply creates or replaces, a stated resourceVersion
/// must match, and every write advances the version, as the API server does.
/// </summary>
internal sealed class FakeConfigMapApi : IKubernetesResourceApi
{
    private readonly Dictionary<string, V1ConfigMap> _maps = new(StringComparer.Ordinal);
    private int _version;

    public List<string> Deleted { get; } = [];

    // Runs before an apply is checked, so a test can move an object's version under a writer that already read it.
    public Action<string>? BeforeApply { get; set; }

    public IReadOnlyCollection<string> Names => _maps.Keys;

    public V1ConfigMap Get(string name) => Clone(_maps["appa/" + name]);

    // Another writer changed the object after the store read it.
    public void Touch(string name) => _maps["appa/" + name].Metadata.ResourceVersion = Next();

    public Task<bool> TryCreateAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The record store writes by server-side apply only.");

    public Task ApplyAsync(IKubernetesObject<V1ObjectMeta> resource, bool force, CancellationToken cancellationToken = default)
    {
        var map = (V1ConfigMap)resource;
        BeforeApply?.Invoke(map.Metadata.Name);
        string key = Key(map);
        string? stated = map.Metadata.ResourceVersion;
        if (_maps.TryGetValue(key, out V1ConfigMap? existing))
        {
            if (stated is not null && stated != existing.Metadata.ResourceVersion)
            {
                throw Failure(HttpStatusCode.Conflict);
            }
        }
        else if (stated is not null)
        {
            throw Failure(HttpStatusCode.NotFound);
        }

        V1ConfigMap stored = Clone(map);
        stored.Metadata.ResourceVersion = Next();
        _maps[key] = stored;
        return Task.CompletedTask;
    }

    public Task JsonPatchAsync(IKubernetesObject<V1ObjectMeta> resource, string patch, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default) =>
        Task.FromResult<IKubernetesObject<V1ObjectMeta>?>(
            resource is V1ConfigMap && _maps.TryGetValue(Key(resource), out V1ConfigMap? map) ? Clone(map) : null);

    public Task DeleteAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
    {
        if (_maps.Remove(Key(resource)))
        {
            Deleted.Add(resource.Metadata.Name);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
        string namespaceName, string? resourceLabel, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        string namespaceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<V1PodList> ListPodsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<V1Endpoints?> ReadEndpointsAsync(string namespaceName, string serviceName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<V1Pod> WatchPodsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private string Next() => (++_version).ToString(CultureInfo.InvariantCulture);

    private static string Key(IKubernetesObject<V1ObjectMeta> resource) => resource.Metadata.NamespaceProperty + "/" + resource.Metadata.Name;

    private static V1ConfigMap Clone(V1ConfigMap map) => new()
    {
        ApiVersion = map.ApiVersion,
        Kind = map.Kind,
        Metadata = new V1ObjectMeta
        {
            Name = map.Metadata.Name,
            NamespaceProperty = map.Metadata.NamespaceProperty,
            ResourceVersion = map.Metadata.ResourceVersion,
            Labels = map.Metadata.Labels is null ? null : new Dictionary<string, string>(map.Metadata.Labels, StringComparer.Ordinal),
            Annotations = map.Metadata.Annotations is null ? null : new Dictionary<string, string>(map.Metadata.Annotations, StringComparer.Ordinal),
        },
        Data = map.Data is null ? null : new Dictionary<string, string>(map.Data, StringComparer.Ordinal),
    };

    private static HttpOperationException Failure(HttpStatusCode status) =>
        new($"Simulated ConfigMap {status}.")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), string.Empty),
        };
}
