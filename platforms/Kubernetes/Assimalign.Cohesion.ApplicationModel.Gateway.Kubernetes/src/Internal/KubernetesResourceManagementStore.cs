using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesResourceManagementStore : IProjectedResourceManagementStore
{
    internal const string StateKey = "coordination.json";
    internal const string ProjectionKey = "runtime-state.json";
    private readonly IKubernetesResourceApi _api;
    private readonly Func<ApplicationName, (string Namespace, string Owner)> _scope;

    public KubernetesResourceManagementStore(IKubernetesResourceApi api, Func<ApplicationName, (string Namespace, string Owner)> scope)
    {
        _api = api;
        _scope = scope;
    }

    public async ValueTask<ResourceManagementDocument?> ReadAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        V1Secret candidate = Create(application, resource);
        if (await _api.ReadAsync(candidate, cancellationToken).ConfigureAwait(false) is not V1Secret existing) { return null; }
        Validate(existing, candidate);
        return existing.Data?.TryGetValue(StateKey, out byte[]? content) is true
            ? new ResourceManagementDocument(existing.Metadata.ResourceVersion, (byte[])content.Clone()) : null;
    }

    public ValueTask<bool> TryWriteAsync(ApplicationName application, ResourceName resource, string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        TryWriteProjectionAsync(application, resource, expectedVersion, content, default, cancellationToken);

    public async ValueTask<bool> TryWriteProjectionAsync(ApplicationName application, ResourceName resource, string? expectedVersion,
        ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> projection, CancellationToken cancellationToken = default)
    {
        V1Secret candidate = Create(application, resource);
        V1Secret? existing = await _api.ReadAsync(candidate, cancellationToken).ConfigureAwait(false) as V1Secret;
        if (existing is not null)
        {
            Validate(existing, candidate);
            bool hasIntent = existing.Data?.ContainsKey(StateKey) is true;
            if ((expectedVersion is null && hasIntent) || (expectedVersion is not null && existing.Metadata.ResourceVersion != expectedVersion)) { return false; }
            candidate.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            candidate.Metadata.Uid = existing.Metadata.Uid;
            candidate.Metadata.Annotations = new Dictionary<string, string>(existing.Metadata.Annotations);
            candidate.Data = existing.Data is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(existing.Data);
        }
        else if (expectedVersion is not null) { return false; }
        candidate.Data ??= new Dictionary<string, byte[]>();
        candidate.Data[StateKey] = content.ToArray();
        if (!projection.IsEmpty) { candidate.Data[ProjectionKey] = projection.ToArray(); }
        if (existing is null) { return await _api.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false); }
        try
        {
            await _api.ApplyAsync(candidate, force: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) { return false; }
    }

    private V1Secret Create(ApplicationName application, ResourceName resource)
    {
        (string namespaceName, string owner) = _scope(application);
        string resourceName = KubernetesMetadata.ResourceName(resource);
        return new V1Secret
        {
            ApiVersion = V1Secret.KubeApiVersion, Kind = V1Secret.KubeKind, Type = "Opaque",
            Metadata = KubernetesMetadata.CreateObjectMeta(KubernetesMetadata.ChildName(resourceName, "management"), namespaceName, resourceName, "management/v1", owner)
        };
    }

    private static void Validate(V1Secret existing, V1Secret candidate)
    {
        if (existing.Type != "Opaque" || string.IsNullOrWhiteSpace(existing.Metadata.ResourceVersion) ||
            existing.Metadata.Annotations?.TryGetValue(KubernetesMetadata.OwnerAnnotation, out string? owner) != true ||
            owner != candidate.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation] ||
            existing.Metadata.Labels?.TryGetValue(KubernetesMetadata.ResourceLabel, out string? resource) != true ||
            resource != candidate.Metadata.Labels[KubernetesMetadata.ResourceLabel])
        {
            throw new InvalidDataException("The resource management Secret has an invalid owner, resource identity or version.");
        }
    }
}
