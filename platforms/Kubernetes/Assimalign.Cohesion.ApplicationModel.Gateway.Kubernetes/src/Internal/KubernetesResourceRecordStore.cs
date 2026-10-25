using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Keeps deployment records as ConfigMaps in the application's namespace: one holding the deployment and override
/// layers and the platform surface overrides, named for the pod template that reads it, and one per resource holding
/// its claims and naming the current generation and record ConfigMap.
/// </summary>
/// <remarks>
/// <para>
/// The pod template projects <c>&lt;resource&gt;-record-&lt;key&gt;</c> (its two layer keys) and
/// <c>&lt;resource&gt;-claims</c> into one directory. The key is the start of the plan's pod-template hash
/// (<see cref="RecordKey"/>), not the generation. A new generation that leaves the pod template as it was, such as a
/// changed autoscaler bound, keeps the ConfigMap: the pods stay, and the overrides the generation discards leave them
/// when the kubelet next refreshes the projection, within about a minute. A plan change the pods do read is a new name
/// and therefore a rollout, in which each ReplicaSet's pods keep reading their own record.
/// </para>
/// <para>
/// The objects carry <see cref="RecordLabel"/>, not the plan's resource label, so the plan controller's stale-object
/// pruning never removes them. When the key moves, the previous record ConfigMap is kept for the pods of a rollout still
/// in progress and the one before it is deleted; namespace teardown removes the rest.
/// </para>
/// </remarks>
internal sealed class KubernetesResourceRecordStore : IResourceRecordStore
{
    internal const string InstanceDirectory = "/var/run/cohesion-config";
    internal const string RecordLabel = "cohesion.io/record";
    internal const string GenerationAnnotation = "cohesion.io/generation";
    internal const string RecordKeyAnnotation = "cohesion.io/record-key";
    internal const string PreviousRecordKeyAnnotation = "cohesion.io/previous-record-key";
    // The pod template annotation that rolls a workload when what the record sets for restart-class sections changes.
    internal const string RestartDigestAnnotation = "cohesion.io/restart-digest";
    internal const string NoRestartDigest = "none";
    internal const string DeploymentKey = "deployment.json";
    internal const string OverrideKey = "override.json";
    private const string claimKeyPrefix = "claims.";
    private const string platformKeyPrefix = "platform.";
    private const string layerKeySuffix = ".json";
    // A record whose claims ConfigMap names no generation never matches a computed one, so the next deployment rewrites it.
    private const string unknownGeneration = "unknown";
    private const int recordKeyLength = 16;

    private readonly IKubernetesResourceApi _api;
    private readonly Func<ApplicationName, (string Namespace, string Owner)> _scope;
    private readonly Func<ApplicationName, ResourceName, string?> _recordKey;
    // One gateway process writes an application's records; its deployment and management writes are serialized here,
    // and resourceVersion preconditions refuse a write that raced another process.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    // recordKey names the record ConfigMap the resource's current plan projects, or null when the gateway has not
    // validated that plan; a new generation is then written under its own name.
    public KubernetesResourceRecordStore(
        IKubernetesResourceApi api,
        Func<ApplicationName, (string Namespace, string Owner)> scope,
        Func<ApplicationName, ResourceName, string?> recordKey)
    {
        _api = api;
        _scope = scope;
        _recordKey = recordKey;
    }

    public static string RecordKey(string planHash) => planHash[..recordKeyLength];

    public static string RecordName(string resource, string key) => resource + "-record-" + key;

    public static string ClaimsName(string resource) => resource + "-claims";

    public string GetInstanceDirectory(ApplicationName application, ResourceName resource) => InstanceDirectory;

    public async ValueTask<ResourceRecord?> ReadAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default)
    {
        (string namespaceName, string owner) = _scope(application);
        string name = KubernetesMetadata.ResourceName(resource);
        V1ConfigMap? claims = await ReadOwnedAsync(ClaimsName(name), namespaceName, name, owner, cancellationToken).ConfigureAwait(false);
        if (claims is null)
        {
            return null;
        }

        string generation = ReadAnnotation(claims, GenerationAnnotation) ?? unknownGeneration;
        V1ConfigMap? record = ReadAnnotation(claims, RecordKeyAnnotation) is string key
            ? await ReadOwnedAsync(RecordName(name, key), namespaceName, name, owner, cancellationToken).ConfigureAwait(false)
            : null;
        return Compose(generation, record, claims);
    }

    public async ValueTask<bool> TryWriteAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord record,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        SemaphoreSlim gate = _gates.GetOrAdd(application.ToString() + "/" + resource.ToString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteAsync(application, resource, record, expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<bool> WriteAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord record,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        (string namespaceName, string owner) = _scope(application);
        string name = KubernetesMetadata.ResourceName(resource);
        V1ConfigMap? claims = await ReadOwnedAsync(ClaimsName(name), namespaceName, name, owner, cancellationToken).ConfigureAwait(false);
        string? currentGeneration = claims is null ? null : ReadAnnotation(claims, GenerationAnnotation);
        string? currentKey = claims is null ? null : ReadAnnotation(claims, RecordKeyAnnotation);
        V1ConfigMap? currentRecord = currentKey is null
            ? null
            : await ReadOwnedAsync(RecordName(name, currentKey), namespaceName, name, owner, cancellationToken).ConfigureAwait(false);
        if (expectedVersion is not null &&
            (claims is null || !string.Equals(Compose(currentGeneration ?? unknownGeneration, currentRecord, claims).Version, expectedVersion, StringComparison.Ordinal)))
        {
            return false;
        }

        // A generation keeps its ConfigMap. A new one is written to the ConfigMap its plan's pod template projects, which
        // is the current one when the generation changed nothing the pods read.
        string key = currentKey is not null && string.Equals(currentGeneration, record.Generation, StringComparison.Ordinal)
            ? currentKey
            : _recordKey(application, resource) ?? record.Generation;
        V1ConfigMap? target = string.Equals(key, currentKey, StringComparison.Ordinal)
            ? currentRecord
            : await ReadOwnedAsync(RecordName(name, key), namespaceName, name, owner, cancellationToken).ConfigureAwait(false);

        // The record ConfigMap first, then the claims ConfigMap that points at it; a write that loses a race on either is
        // refused, and the caller reads the record again.
        V1ConfigMap desiredRecord = CreateRecordMap(RecordName(name, key), namespaceName, name, owner, record);
        if (!await TryPutAsync(desiredRecord, target, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        bool moved = currentKey is not null && !string.Equals(currentKey, key, StringComparison.Ordinal);
        string? retired = claims is null ? null : ReadAnnotation(claims, PreviousRecordKeyAnnotation);
        string? previous = moved ? currentKey : retired;
        V1ConfigMap desiredClaims = CreateClaimsMap(
            ClaimsName(name), namespaceName, name, owner, record, key, string.Equals(previous, key, StringComparison.Ordinal) ? null : previous);
        if (!await TryPutAsync(desiredClaims, claims, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Pods of a rollout still in progress read the previous ConfigMap; the one before it has no readers left, unless
        // the plan returned to it.
        if (moved && retired is not null && !string.Equals(retired, key, StringComparison.Ordinal))
        {
            await DeleteQuietlyAsync(RecordName(name, retired), namespaceName, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async ValueTask DeleteAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        (string namespaceName, string owner) = _scope(application);
        string name = KubernetesMetadata.ResourceName(resource);
        V1ConfigMap? claims = await ReadOwnedAsync(ClaimsName(name), namespaceName, name, owner, cancellationToken).ConfigureAwait(false);
        if (claims is null)
        {
            return;
        }

        foreach (string annotation in new[] { RecordKeyAnnotation, PreviousRecordKeyAnnotation })
        {
            if (ReadAnnotation(claims, annotation) is string key)
            {
                await DeleteQuietlyAsync(RecordName(name, key), namespaceName, cancellationToken).ConfigureAwait(false);
            }
        }

        await DeleteQuietlyAsync(ClaimsName(name), namespaceName, cancellationToken).ConfigureAwait(false);
    }

    private static ResourceRecord Compose(string generation, V1ConfigMap? record, V1ConfigMap claims)
    {
        var claimLayers = new Dictionary<ApplicationName, ReadOnlyMemory<byte>>();
        foreach ((string key, string value) in claims.Data ?? new Dictionary<string, string>())
        {
            if (key.StartsWith(claimKeyPrefix, StringComparison.Ordinal) &&
                key.EndsWith(layerKeySuffix, StringComparison.Ordinal) &&
                ApplicationName.IsValid(key[claimKeyPrefix.Length..^layerKeySuffix.Length], out _))
            {
                claimLayers.Add(key[claimKeyPrefix.Length..^layerKeySuffix.Length], Encoding.UTF8.GetBytes(value));
            }
        }

        var platform = new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>();
        IDictionary<string, string> data = record?.Data ?? new Dictionary<string, string>();
        foreach ((string key, string value) in data)
        {
            // platform.<platform>.<name>.json, so the key needs no character a ConfigMap key refuses.
            if (key.StartsWith(platformKeyPrefix, StringComparison.Ordinal) && key.EndsWith(layerKeySuffix, StringComparison.Ordinal))
            {
                string surface = key[platformKeyPrefix.Length..^layerKeySuffix.Length];
                int separator = surface.IndexOf('.', StringComparison.Ordinal);
                if (separator > 0)
                {
                    string candidate = surface[..separator] + "/" + surface[(separator + 1)..];
                    if (ResourceExtensionKey.IsValid(candidate, out _))
                    {
                        platform.Add(candidate, Encoding.UTF8.GetBytes(value));
                    }
                }
            }
        }

        return ResourceRecord.Create(
            generation,
            data.TryGetValue(DeploymentKey, out string? deployment) ? Encoding.UTF8.GetBytes(deployment) : default,
            claimLayers,
            data.TryGetValue(OverrideKey, out string? @override) ? Encoding.UTF8.GetBytes(@override) : default,
            platform);
    }

    private static V1ConfigMap CreateRecordMap(string name, string namespaceName, string resource, string owner, ResourceRecord record)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!record.Deployment.IsEmpty)
        {
            data[DeploymentKey] = Encoding.UTF8.GetString(record.Deployment.Span);
        }

        if (!record.Override.IsEmpty)
        {
            data[OverrideKey] = Encoding.UTF8.GetString(record.Override.Span);
        }

        foreach ((ResourceExtensionKey key, ReadOnlyMemory<byte> patch) in record.PlatformOverrides)
        {
            data[platformKeyPrefix + key.Value.Replace('/', '.') + layerKeySuffix] = Encoding.UTF8.GetString(patch.Span);
        }

        V1ConfigMap map = CreateMap(name, namespaceName, resource, owner);
        map.Data = data;
        return map;
    }

    private static V1ConfigMap CreateClaimsMap(
        string name,
        string namespaceName,
        string resource,
        string owner,
        ResourceRecord record,
        string key,
        string? previousKey)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((ApplicationName claimant, ReadOnlyMemory<byte> layer) in record.Claims)
        {
            data[claimKeyPrefix + claimant.ToString() + layerKeySuffix] = Encoding.UTF8.GetString(layer.Span);
        }

        V1ConfigMap map = CreateMap(name, namespaceName, resource, owner);
        map.Data = data;
        map.Metadata.Annotations[GenerationAnnotation] = record.Generation;
        map.Metadata.Annotations[RecordKeyAnnotation] = key;
        if (previousKey is not null)
        {
            map.Metadata.Annotations[PreviousRecordKeyAnnotation] = previousKey;
        }

        return map;
    }

    private static V1ConfigMap CreateMap(string name, string namespaceName, string resource, string owner) => new()
    {
        ApiVersion = V1ConfigMap.KubeApiVersion,
        Kind = V1ConfigMap.KubeKind,
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue,
                [RecordLabel] = resource,
            },
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesMetadata.OwnerAnnotation] = owner,
            },
        },
    };

    // Server-side apply both creates and replaces, so the gateway owns every key by apply and a key the record no
    // longer has is removed; a create would own its keys by update, and a later apply would leave them in place. An
    // existing object is replaced only at the version that was read: false when another writer got there first.
    private async Task<bool> TryPutAsync(V1ConfigMap desired, V1ConfigMap? existing, CancellationToken cancellationToken)
    {
        desired.Metadata.ResourceVersion = existing?.Metadata.ResourceVersion;
        try
        {
            await _api.ApplyAsync(desired, force: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<V1ConfigMap?> ReadOwnedAsync(
        string name,
        string namespaceName,
        string resource,
        string owner,
        CancellationToken cancellationToken)
    {
        if (await _api.ReadAsync(CreateMap(name, namespaceName, resource, owner), cancellationToken).ConfigureAwait(false) is not V1ConfigMap map)
        {
            return null;
        }

        if (map.Metadata?.Labels?.TryGetValue(RecordLabel, out string? labelled) != true || labelled != resource ||
            map.Metadata.Annotations?.TryGetValue(KubernetesMetadata.OwnerAnnotation, out string? recorded) != true || recorded != owner)
        {
            throw new InvalidDataException(
                $"Kubernetes ConfigMap/{name} in namespace '{namespaceName}' is not the deployment record of resource " +
                $"'{resource}' for this gateway's owner. Remove it, or tear down the application that owns it.");
        }

        return map;
    }

    // Deleting an absent ConfigMap succeeds, so a record ConfigMap retired twice or never written is not an error.
    private Task DeleteQuietlyAsync(string name, string namespaceName, CancellationToken cancellationToken) =>
        _api.DeleteAsync(CreateMap(name, namespaceName, string.Empty, string.Empty), cancellationToken);

    private static string? ReadAnnotation(V1ConfigMap map, string name) =>
        map.Metadata?.Annotations is { } annotations && annotations.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;
}
