using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

internal sealed class FakeGatewayTrustKeyApi : IKubernetesResourceApi, IDisposable
{
    private V1Secret? _stored;
    private int _version;

    public int Reads { get; private set; }
    public int Creates { get; private set; }
    public int Applies { get; private set; }
    public bool ConflictEveryCreate { get; init; }
    public bool ConflictEveryApply { get; init; }
    public Action<FakeGatewayTrustKeyApi>? BeforeCreate { get; set; }
    public Action<FakeGatewayTrustKeyApi>? BeforeApply { get; set; }
    public string? LastApplyResourceVersion { get; private set; }
    public bool? LastApplyForce { get; private set; }
    public string? LastReadNamespace { get; private set; }
    public string? LastReadName { get; private set; }

    public V1Secret Snapshot() => Clone(_stored ?? throw new InvalidOperationException("No Secret is stored."));

    public void Seed(V1Secret secret)
    {
        V1Secret copy = Clone(secret);
        ClearStored();
        copy.Metadata.ResourceVersion = (++_version).ToString(CultureInfo.InvariantCulture);
        _stored = copy;
    }

    public Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        LastReadName = resource.Metadata.Name;
        LastReadNamespace = resource.Metadata.NamespaceProperty;
        return Task.FromResult<IKubernetesObject<V1ObjectMeta>?>(_stored is null ? null : Clone(_stored));
    }

    public Task<bool> TryCreateAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Creates++;
        BeforeCreate?.Invoke(this);
        if (_stored is not null || ConflictEveryCreate)
        {
            return Task.FromResult(false);
        }

        Seed((V1Secret)resource);
        return Task.FromResult(true);
    }

    public Task ApplyAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        bool force,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Applies++;
        LastApplyResourceVersion = resource.Metadata.ResourceVersion;
        LastApplyForce = force;
        BeforeApply?.Invoke(this);
        if (ConflictEveryApply || _stored is null
            || !string.Equals(resource.Metadata.ResourceVersion, _stored.Metadata.ResourceVersion, StringComparison.Ordinal))
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Conflict);
            throw new HttpOperationException("Simulated Secret resourceVersion conflict.")
            {
                Response = new HttpResponseMessageWrapper(response, string.Empty),
            };
        }

        Seed((V1Secret)resource);
        return Task.CompletedTask;
    }

    public Task JsonPatchAsync(IKubernetesObject<V1ObjectMeta> resource, string patch, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
        string namespaceName, string? resourceLabel, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        string namespaceName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<V1PodList> ListPodsAsync(
        string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<V1Endpoints?> ReadEndpointsAsync(
        string namespaceName, string serviceName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<V1Pod> WatchPodsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public void Dispose() => ClearStored();

    private static V1Secret Clone(V1Secret secret) => new()
    {
        ApiVersion = secret.ApiVersion,
        Kind = secret.Kind,
        Type = secret.Type,
        Metadata = new V1ObjectMeta
        {
            Name = secret.Metadata.Name,
            NamespaceProperty = secret.Metadata.NamespaceProperty,
            ResourceVersion = secret.Metadata.ResourceVersion,
            Uid = secret.Metadata.Uid,
            Labels = secret.Metadata.Labels is null ? null : new Dictionary<string, string>(secret.Metadata.Labels),
            Annotations = secret.Metadata.Annotations is null ? null : new Dictionary<string, string>(secret.Metadata.Annotations),
        },
        Data = secret.Data?.ToDictionary(pair => pair.Key, pair => pair.Value.AsSpan().ToArray(), StringComparer.Ordinal),
    };

    private void ClearStored()
    {
        if (_stored?.Data is not null)
        {
            foreach (byte[] value in _stored.Data.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }

        _stored = null;
    }
}
