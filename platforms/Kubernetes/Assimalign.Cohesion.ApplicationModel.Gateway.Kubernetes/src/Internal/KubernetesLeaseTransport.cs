using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

/// <summary>
/// The transport between a namespace's instances and their gateway: it watches the namespace's
/// instance Leases and hands the gateway each publication and renewal they carry, then writes the
/// gateway's answer back on the Lease.
/// </summary>
/// <remarks>
/// <para>
/// A Lease whose answer is not for the key its manifest names is a publication: the gateway's answer,
/// or its refusal, goes into the Lease's annotations, where the instance waits for it. A Lease whose
/// answer is current and whose renew time moved is a renewal; when the gateway does not know the
/// instance, the transport clears the answer, so the instance's next renewal finds itself unknown and
/// it publishes again.
/// </para>
/// <para>
/// The watch is listed again from the start whenever it ends or fails, so a gateway that restarts reads
/// every Lease once: a Lease an earlier gateway answered is a renewal, and the new gateway keeps it or
/// sends it to publish again. Events are handled one at a time on the transport's own flow.
/// </para>
/// </remarks>
internal sealed class KubernetesLeaseTransport
{
    private const string fieldManager = "cohesion-gateway";

    private static readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(1);

    private readonly IKubernetes _client;
    private readonly string _namespace;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private readonly Dictionary<string, DateTime?> _renewals = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _running;
    private Task _loop = Task.CompletedTask;

    public KubernetesLeaseTransport(IKubernetes client, string ns)
    {
        _client = client;
        _namespace = ns;
    }

    public void Start(IApplicationInstancePublisher gateway)
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException(ErrorMessages.TransportAlreadyStarted);
            }

            var running = new CancellationTokenSource();
            _running = running;
            _renewals.Clear();

            // Task.Run captures the caller's execution context: the gateway's.
            _loop = Task.Run(() => RunAsync(gateway, running.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? running;
        Task loop;
        lock (_lock)
        {
            running = _running;
            loop = _loop;
            _running = null;
        }

        if (running is null)
        {
            return;
        }

        await running.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        running.Dispose();
    }

    private async Task RunAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                V1LeaseList leases = await _client.CoordinationV1.ListNamespacedLeaseAsync(
                    _namespace,
                    labelSelector: KubernetesProtocol.InstanceLeaseSelector,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (V1Lease lease in leases.Items)
                {
                    await HandleAsync(gateway, lease, cancellationToken).ConfigureAwait(false);
                }

                var watch = _client.CoordinationV1.ListNamespacedLeaseWithHttpMessagesAsync(
                    _namespace,
                    labelSelector: KubernetesProtocol.InstanceLeaseSelector,
                    resourceVersion: leases.Metadata?.ResourceVersion,
                    watch: true,
                    cancellationToken: cancellationToken);
                await foreach ((WatchEventType type, V1Lease lease) in watch.WatchAsync<V1Lease, V1LeaseList>(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (type is WatchEventType.Added or WatchEventType.Modified)
                    {
                        await HandleAsync(gateway, lease, cancellationToken).ConfigureAwait(false);
                    }
                    else if (type == WatchEventType.Deleted && lease.Metadata?.Name is { } name)
                    {
                        _renewals.Remove(name);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Deliberately broad: a watch that ends or fails is listed again; the API server reports
                // each failure in its own terms.
                try
                {
                    await Task.Delay(_retryInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleAsync(IApplicationInstancePublisher gateway, V1Lease lease, CancellationToken cancellationToken)
    {
        string? name = lease.Metadata?.Name;
        string? manifestJson = Annotation(lease, KubernetesProtocol.ManifestAnnotation);
        string? key = Annotation(lease, KubernetesProtocol.InstanceKeyAnnotation);
        if (name is null || manifestJson is null || key is null || Annotation(lease, KubernetesProtocol.RefusalKeyAnnotation) == key)
        {
            return;
        }

        ApplicationInstanceManifest? manifest;
        try
        {
            manifest = _json.Deserialize<ApplicationInstanceManifest>(Encoding.UTF8.GetBytes(manifestJson));
        }
        catch (ApplicationContentException exception)
        {
            await RefuseAsync(name, key, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (manifest is null)
        {
            return;
        }

        if (Annotation(lease, KubernetesProtocol.PublicationKeyAnnotation) != key)
        {
            await PublishAsync(gateway, name, key, manifest, lease.Spec?.RenewTime, cancellationToken).ConfigureAwait(false);
            return;
        }

        // An answered Lease whose renew time has not moved is the transport's own write, or one already
        // handled.
        DateTime? renewed = lease.Spec?.RenewTime;
        if (_renewals.TryGetValue(name, out DateTime? last) && last == renewed)
        {
            return;
        }

        _renewals[name] = renewed;
        bool known;
        try
        {
            known = await gateway.RenewAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationGatewayException)
        {
            // The gateway is not receiving; the instance renews again on its next tick.
            return;
        }

        if (!known)
        {
            // The instance's next renewal finds no answer for itself, and it publishes again.
            await PatchAnnotationsAsync(name, new JsonObject
            {
                [KubernetesProtocol.PublicationAnnotation] = null,
                [KubernetesProtocol.PublicationKeyAnnotation] = null,
            }, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(IApplicationInstancePublisher gateway, string name, string key, ApplicationInstanceManifest manifest, DateTime? renewed, CancellationToken cancellationToken)
    {
        ApplicationInstancePublication publication;
        try
        {
            publication = await gateway.PublishAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately broad: whatever refused the publication is the instance's answer.
            await RefuseAsync(name, key, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        int? leaseSeconds = publication.Lease is { } lease ? Math.Max(1, (int)Math.Ceiling(lease.TotalSeconds)) : null;
        await PatchAnnotationsAsync(name, new JsonObject
        {
            [KubernetesProtocol.PublicationAnnotation] = Encoding.UTF8.GetString(_json.Serialize(publication).Span),
            [KubernetesProtocol.PublicationKeyAnnotation] = key,
        }, leaseSeconds, cancellationToken).ConfigureAwait(false);

        // The transport's own write moves no renew time, so it is not taken for a renewal.
        _renewals[name] = renewed;
    }

    private Task RefuseAsync(string name, string key, string reason, CancellationToken cancellationToken)
    {
        return PatchAnnotationsAsync(name, new JsonObject
        {
            [KubernetesProtocol.RefusalAnnotation] = reason,
            [KubernetesProtocol.RefusalKeyAnnotation] = key,
        }, null, cancellationToken);
    }

    private async Task PatchAnnotationsAsync(string name, JsonObject annotations, int? leaseSeconds, CancellationToken cancellationToken)
    {
        var patch = new JsonObject { ["metadata"] = new JsonObject { ["annotations"] = annotations } };
        if (leaseSeconds is { } seconds)
        {
            patch["spec"] = new JsonObject { ["leaseDurationSeconds"] = seconds };
        }

        try
        {
            await _client.CoordinationV1.PatchNamespacedLeaseAsync(new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch), name, _namespace, fieldManager: fieldManager, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (k8s.Autorest.HttpOperationException exception) when (exception.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The pod, and its Lease with it, is gone.
        }
    }

    private static string? Annotation(V1Lease lease, string name)
    {
        return lease.Metadata?.Annotations is { } annotations && annotations.TryGetValue(name, out string? value) ? value : null;
    }
}
