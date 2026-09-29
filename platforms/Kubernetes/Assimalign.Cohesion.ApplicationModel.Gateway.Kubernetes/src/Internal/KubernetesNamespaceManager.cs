using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Manages an owned application namespace through idempotent server-side apply and guarded
/// teardown.
/// </summary>
internal sealed class KubernetesNamespaceManager
{
    private readonly IKubernetes _client;
    private readonly string _fieldManager;

    public KubernetesNamespaceManager(IKubernetes client, string fieldManager)
    {
        _client = client;
        _fieldManager = fieldManager;
    }

    /// <summary>
    /// Ensures the namespace exists and carries the Cohesion owner metadata. A namespace with a
    /// different or missing owner is left untouched unless adoption was explicitly requested.
    /// </summary>
    /// <param name="namespaceName">The RFC 1123 namespace name.</param>
    /// <param name="owner">The application and gateway identity claiming the namespace.</param>
    /// <param name="adopt">Whether an existing foreign or unowned namespace may be adopted.</param>
    /// <param name="cancellationToken">Signals that the operation should be abandoned.</param>
    /// <returns>A task that completes once the namespace exists.</returns>
    public async Task EnsureAsync(
        string namespaceName,
        string owner,
        bool adopt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        V1Namespace? existing = await ReadAsync(namespaceName, cancellationToken).ConfigureAwait(false);
        V1Namespace body = CreateNamespace(namespaceName, owner);
        if (existing is null)
        {
            try
            {
                await _client.CoreV1
                    .CreateNamespaceAsync(
                        body,
                        fieldManager: _fieldManager,
                        fieldValidation: "Strict",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (HttpOperationException conflict) when (conflict.Response.StatusCode == HttpStatusCode.Conflict)
            {
                // Another actor won the create race. Re-read instead of treating that namespace
                // as ours, then apply only after the same ownership/adoption decision.
                existing = await ReadAsync(namespaceName, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    throw new InvalidOperationException(
                        $"Kubernetes namespace '{namespaceName}' was created and removed while " +
                        "ownership was being checked. Retry the operation.",
                        conflict);
                }
            }
        }

        bool force = RequireOwnership(existing, namespaceName, owner, adopt);
        body.Metadata.ResourceVersion = existing.Metadata?.ResourceVersion;
        await ApplyAsync(body, namespaceName, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the namespace only while it is still owned by <paramref name="owner">, silently
    /// tolerating a namespace that no longer exists.
    /// </summary>
    /// <param name="namespaceName">The RFC 1123 namespace name.</param>
    /// <param name="owner">The owner identity expected on the namespace.</param>
    /// <param name="cancellationToken">Bounds how long deletion may take.</param>
    /// <returns>A task that completes once the namespace no longer exists.</returns>
    public async Task DeleteAsync(
        string namespaceName,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        V1Namespace? existing = await ReadAsync(namespaceName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        _ = RequireOwnership(existing, namespaceName, owner, adopt: false);
        string? uid = existing.Metadata?.Uid;
        string? resourceVersion = existing.Metadata?.ResourceVersion;
        var options = new V1DeleteOptions
        {
            Preconditions = uid is null && resourceVersion is null
                ? null
                : new V1Preconditions
                {
                    Uid = uid,
                    ResourceVersion = resourceVersion,
                },
        };

        try
        {
            await _client.CoreV1
                .DeleteNamespaceAsync(
                    namespaceName,
                    body: options,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — deletion is idempotent by contract.
            return;
        }

        while (await ReadAsync(namespaceName, cancellationToken).ConfigureAwait(false) is not null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static V1Namespace CreateNamespace(string namespaceName, string owner) => new()
    {
        ApiVersion = V1Namespace.KubeApiVersion,
        Kind = V1Namespace.KubeKind,
        Metadata = new V1ObjectMeta
        {
            Name = namespaceName,
            Labels = new Dictionary<string, string>
            {
                [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue,
            },
            Annotations = new Dictionary<string, string>
            {
                [KubernetesMetadata.OwnerAnnotation] = owner,
            },
        },
    };

    private async Task<V1Namespace?> ReadAsync(
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.CoreV1
                .ReadNamespaceAsync(namespaceName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task ApplyAsync(
        V1Namespace body,
        string namespaceName,
        bool force,
        CancellationToken cancellationToken)
    {
        var patch = new V1Patch(KubernetesJson.Serialize(body), V1Patch.PatchType.ApplyPatch);
        await _client.CoreV1
            .PatchNamespaceAsync(
                patch,
                namespaceName,
                fieldManager: _fieldManager,
                fieldValidation: "Strict",
                force: force,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool RequireOwnership(
        V1Namespace existing,
        string namespaceName,
        string owner,
        bool adopt)
    {
        string? existingOwner = null;
        if (existing.Metadata?.Annotations is not null)
        {
            _ = existing.Metadata.Annotations.TryGetValue(
                KubernetesMetadata.OwnerAnnotation,
                out existingOwner);
        }

        if (string.Equals(existingOwner, owner, StringComparison.Ordinal))
        {
            return false;
        }

        if (!adopt)
        {
            string ownerDescription = string.IsNullOrWhiteSpace(existingOwner)
                ? "no Cohesion owner"
                : $"owner '{existingOwner}'";
            throw new InvalidOperationException(
                $"Kubernetes namespace '{namespaceName}' has {ownerDescription}; expected '{owner}'. " +
                "Refusing to take ownership. Pass --adopt to adopt the existing namespace explicitly.");
        }

        return true;
    }
}
