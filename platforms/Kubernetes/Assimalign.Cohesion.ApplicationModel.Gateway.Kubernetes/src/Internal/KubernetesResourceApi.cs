using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesResourceApi : IKubernetesResourceApi
{
    private readonly IKubernetes _client;
    private readonly string _fieldManager;

    public KubernetesResourceApi(IKubernetes client, string fieldManager)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldManager);
        _client = client;
        _fieldManager = fieldManager;
    }

    public async Task<bool> TryCreateAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string namespaceName = RequireNamespace(resource);

        try
        {
            switch (resource)
            {
                case V1ConfigMap configMap:
                    await _client.CoreV1.CreateNamespacedConfigMapAsync(configMap, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Secret secret:
                    await _client.CoreV1.CreateNamespacedSecretAsync(secret, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Service service:
                    await _client.CoreV1.CreateNamespacedServiceAsync(service, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1PersistentVolumeClaim claim:
                    await _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(claim, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Deployment deployment:
                    await _client.AppsV1.CreateNamespacedDeploymentAsync(deployment, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1StatefulSet statefulSet:
                    await _client.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1DaemonSet daemonSet:
                    await _client.AppsV1.CreateNamespacedDaemonSetAsync(daemonSet, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Job job:
                    await _client.BatchV1.CreateNamespacedJobAsync(job, namespaceName, fieldManager: _fieldManager, fieldValidation: "Strict", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw Unsupported(resource);
            }

            return true;
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task ApplyAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string name = RequireName(resource);
        var patch = new V1Patch(KubernetesJson.Serialize(resource), V1Patch.PatchType.ApplyPatch);

        switch (resource)
        {
            case V1ConfigMap:
                await _client.CoreV1.PatchNamespacedConfigMapAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Secret:
                await _client.CoreV1.PatchNamespacedSecretAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Service:
                await _client.CoreV1.PatchNamespacedServiceAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1PersistentVolumeClaim:
                await _client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Deployment:
                await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1StatefulSet:
                await _client.AppsV1.PatchNamespacedStatefulSetAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1DaemonSet:
                await _client.AppsV1.PatchNamespacedDaemonSetAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Job:
                await _client.BatchV1.PatchNamespacedJobAsync(patch, name, RequireNamespace(resource), fieldManager: _fieldManager, fieldValidation: "Strict", force: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw Unsupported(resource);
        }
    }

    public async Task JsonPatchAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        string patchDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchDocument);
        string name = RequireName(resource);
        var patch = new V1Patch(patchDocument, V1Patch.PatchType.JsonPatch);

        switch (resource)
        {
            case V1ConfigMap:
                await _client.CoreV1.PatchNamespacedConfigMapAsync(
                    patch,
                    name,
                    RequireNamespace(resource),
                    fieldManager: _fieldManager,
                    fieldValidation: "Strict",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Deployment:
                await _client.AppsV1.PatchNamespacedDeploymentAsync(
                    patch,
                    name,
                    RequireNamespace(resource),
                    fieldManager: _fieldManager,
                    fieldValidation: "Strict",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1StatefulSet:
                await _client.AppsV1.PatchNamespacedStatefulSetAsync(
                    patch,
                    name,
                    RequireNamespace(resource),
                    fieldManager: _fieldManager,
                    fieldValidation: "Strict",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1DaemonSet:
                await _client.AppsV1.PatchNamespacedDaemonSetAsync(
                    patch,
                    name,
                    RequireNamespace(resource),
                    fieldManager: _fieldManager,
                    fieldValidation: "Strict",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case V1Job:
                await _client.BatchV1.PatchNamespacedJobAsync(
                    patch,
                    name,
                    RequireNamespace(resource),
                    fieldManager: _fieldManager,
                    fieldValidation: "Strict",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw Unsupported(resource);
        }
    }

    public async Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string name = RequireName(resource);
        string namespaceName = RequireNamespace(resource);

        try
        {
            return resource switch
            {
                V1ConfigMap => await _client.CoreV1.ReadNamespacedConfigMapAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1Secret => await _client.CoreV1.ReadNamespacedSecretAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1Service => await _client.CoreV1.ReadNamespacedServiceAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1PersistentVolumeClaim => await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1Deployment => await _client.AppsV1.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1StatefulSet => await _client.AppsV1.ReadNamespacedStatefulSetAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1DaemonSet => await _client.AppsV1.ReadNamespacedDaemonSetAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                V1Job => await _client.BatchV1.ReadNamespacedJobAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false),
                _ => throw Unsupported(resource),
            };
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string name = RequireName(resource);
        string namespaceName = RequireNamespace(resource);
        V1DeleteOptions options = CreateDeleteOptions(resource, resource is V1Job);

        try
        {
            switch (resource)
            {
                case V1ConfigMap:
                    await _client.CoreV1.DeleteNamespacedConfigMapAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Secret:
                    await _client.CoreV1.DeleteNamespacedSecretAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Service:
                    await _client.CoreV1.DeleteNamespacedServiceAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1PersistentVolumeClaim:
                    await _client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Deployment:
                    await _client.AppsV1.DeleteNamespacedDeploymentAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1StatefulSet:
                    await _client.AppsV1.DeleteNamespacedStatefulSetAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1DaemonSet:
                    await _client.AppsV1.DeleteNamespacedDaemonSetAsync(name, namespaceName, body: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case V1Job:
                    await _client.BatchV1.DeleteNamespacedJobAsync(
                        name,
                        namespaceName,
                        body: options,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw Unsupported(resource);
            }
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleting an already absent desired object is successful.
        }
    }

    private static V1DeleteOptions CreateDeleteOptions(
        IKubernetesObject<V1ObjectMeta> resource,
        bool foreground)
    {
        string? uid = resource.Metadata?.Uid;
        string? resourceVersion = resource.Metadata?.ResourceVersion;
        return new V1DeleteOptions
        {
            Preconditions = uid is null && resourceVersion is null
                ? null
                : new V1Preconditions
                {
                    Uid = uid,
                    ResourceVersion = resourceVersion,
                },
            PropagationPolicy = foreground ? "Foreground" : null,
        };
    }

    public async Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
        string namespaceName,
        string? resourceLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        string? labelSelector = null;
        if (resourceLabel is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceLabel);
            labelSelector = $"{KubernetesMetadata.ResourceLabel}={resourceLabel}";
        }
        var objects = new List<IKubernetesObject<V1ObjectMeta>>();

        V1ConfigMapList configMaps = await _client.CoreV1
            .ListNamespacedConfigMapAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, configMaps.Items);

        V1SecretList secrets = await _client.CoreV1
            .ListNamespacedSecretAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, secrets.Items);

        V1ServiceList services = await _client.CoreV1
            .ListNamespacedServiceAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, services.Items);

        V1PersistentVolumeClaimList claims = await _client.CoreV1
            .ListNamespacedPersistentVolumeClaimAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, claims.Items);

        V1DeploymentList deployments = await _client.AppsV1
            .ListNamespacedDeploymentAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, deployments.Items);

        V1StatefulSetList statefulSets = await _client.AppsV1
            .ListNamespacedStatefulSetAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, statefulSets.Items);

        V1DaemonSetList daemonSets = await _client.AppsV1
            .ListNamespacedDaemonSetAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, daemonSets.Items);

        V1JobList jobs = await _client.BatchV1
            .ListNamespacedJobAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AddItems(objects, jobs.Items);

        return objects;
    }

    public async Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        V1PersistentVolumeClaimList claims = await _client.CoreV1
            .ListNamespacedPersistentVolumeClaimAsync(
                namespaceName,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return [.. claims.Items];
    }

    public Task<V1PodList> ListPodsAsync(
        string namespaceName,
        string? labelSelector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        return _client.CoreV1.ListNamespacedPodAsync(
            namespaceName,
            labelSelector: labelSelector,
            cancellationToken: cancellationToken);
    }

    public async Task<V1Endpoints?> ReadEndpointsAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        try
        {
            return await _client.CoreV1
                .ReadNamespacedEndpointsAsync(
                    serviceName,
                    namespaceName,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException exception)
            when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<V1Pod> WatchPodsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string labelSelector =
            $"{KubernetesMetadata.ManagedByLabel}={KubernetesMetadata.ManagedByValue}";
        V1PodList initial = await _client.CoreV1
            .ListPodForAllNamespacesAsync(
                labelSelector: labelSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        for (int index = 0; index < initial.Items.Count; index++)
        {
            yield return initial.Items[index];
        }

        ExceptionDispatchInfo? failure = null;
        var response = _client.CoreV1.ListPodForAllNamespacesWithHttpMessagesAsync(
            allowWatchBookmarks: true,
            labelSelector: labelSelector,
            resourceVersion: initial.Metadata?.ResourceVersion,
            timeoutSeconds: 300,
            watch: true,
            cancellationToken: cancellationToken);
        await foreach ((WatchEventType _, V1Pod pod) in response
            .WatchAsync<V1Pod, V1PodList>(
                exception => failure = ExceptionDispatchInfo.Capture(exception),
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return pod;
        }

        failure?.Throw();
    }

    private static string RequireName(IKubernetesObject<V1ObjectMeta> resource) =>
        resource.Metadata?.Name ?? throw new InvalidOperationException("A Kubernetes resource must have metadata.name.");

    private static string RequireNamespace(IKubernetesObject<V1ObjectMeta> resource) =>
        resource.Metadata?.NamespaceProperty ?? throw new InvalidOperationException("A Kubernetes resource must have metadata.namespace.");

    private static void AddItems<T>(
        ICollection<IKubernetesObject<V1ObjectMeta>> destination,
        IEnumerable<T>? items)
        where T : IKubernetesObject<V1ObjectMeta>
    {
        if (items is null)
        {
            return;
        }

        foreach (T item in items)
        {
            destination.Add(item);
        }
    }

    private static NotSupportedException Unsupported(IKubernetesObject<V1ObjectMeta> resource) =>
        new($"Kubernetes resource type '{resource.GetType().Name}' is not supported by the plan controller.");
}
