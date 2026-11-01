using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Hosting.Platforms;
using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;

/// <summary>
/// The Kubernetes platform's gateway client. It publishes through a Lease named after the instance's
/// pod and waits for the gateway's answer on it, renews through the Lease's renew time, and serves the
/// control plane on the pod's control-plane port.
/// </summary>
/// <remarks>
/// <para>
/// A publication writes the manifest and the instance's key to the Lease and clears any earlier
/// answer, so the gateway that watches the namespace answers it, or refuses it, in annotations. The
/// manifest names the pod's control-plane port, behind the API server's pod proxy, as its control
/// plane's address.
/// </para>
/// <para>
/// A renewal bumps the renew time while the Lease still carries the gateway's answer for this
/// instance. A gateway that no longer knows the instance clears the answer, so the next renewal
/// reports it unknown and the instance publishes again.
/// </para>
/// <para>
/// The Lease belongs to the pod, so the cluster removes it with the pod. The pod reaches the API server
/// with its service account; outside a cluster, as in a test, it uses the kubeconfig.
/// </para>
/// </remarks>
internal sealed class KubernetesGatewayClient : IApplicationInstanceGatewayClient
{
    private const string fieldManager = "cohesion-instance";

    private static readonly TimeSpan _answerPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _answerTimeout = TimeSpan.FromSeconds(60);

    private readonly KubernetesInstanceEnvironment _environment;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private readonly Lock _lock = new();
    private IKubernetes? _client;
    private InstanceControlPlaneListener? _listener;

    public KubernetesGatewayClient(KubernetesInstanceEnvironment environment)
    {
        _environment = environment;
    }

    public PlatformName Platform => KubernetesProtocol.PlatformName;

    /// <exception cref="InvalidOperationException">
    /// The environment names no gateway endpoint or pod, the gateway refused the publication, or no
    /// gateway answered it in time.
    /// </exception>
    public async ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        (string ns, string pod) = ResolvePod();
        string key = manifest.Key.ToString();
        ApplicationInstanceManifest published = manifest with
        {
            ControlPlaneEndpoint = KubernetesProtocol.CreateControlPlaneEndpoint(ns, pod, _environment.ControlPlanePort),
        };

        IKubernetes client = GetClient();
        await WriteManifestAsync(client, ns, pod, key, Encoding.UTF8.GetString(_json.Serialize(published).Span), cancellationToken).ConfigureAwait(false);
        return await WaitForAnswerAsync(client, ns, pod, key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        (string ns, string pod) = ResolvePod();
        IKubernetes client = GetClient();
        V1Lease? lease = await ReadLeaseAsync(client, ns, pod, cancellationToken).ConfigureAwait(false);
        if (lease is null || Annotation(lease, KubernetesProtocol.PublicationKeyAnnotation) != manifest.Key.ToString())
        {
            return false;
        }

        var patch = new JsonObject
        {
            ["spec"] = new JsonObject { ["renewTime"] = Now() },
        };
        await client.CoordinationV1.PatchNamespacedLeaseAsync(new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch), pod, ns, fieldManager: fieldManager, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <exception cref="InvalidOperationException">The client already serves a control plane.</exception>
    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        lock (_lock)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException(ErrorMessages.ClientAlreadyStarted);
            }

            _listener = InstanceControlPlaneListener.Start(_environment.ControlPlanePort, controlPlane);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        InstanceControlPlaneListener? listener;
        lock (_lock)
        {
            listener = _listener;
            _listener = null;
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The pod's Lease carries the manifest and clears an earlier answer, so the gateway answers again.
    private async Task WriteManifestAsync(IKubernetes client, string ns, string pod, string key, string manifest, CancellationToken cancellationToken)
    {
        var lease = new V1Lease
        {
            ApiVersion = "coordination.k8s.io/v1",
            Kind = "Lease",
            Metadata = new V1ObjectMeta
            {
                Name = pod,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    [KubernetesProtocol.ManagedByLabel] = KubernetesProtocol.ManagedByValue,
                    [KubernetesProtocol.RoleLabel] = KubernetesProtocol.InstanceRole,
                },
                Annotations = new Dictionary<string, string>
                {
                    [KubernetesProtocol.ManifestAnnotation] = manifest,
                    [KubernetesProtocol.InstanceKeyAnnotation] = key,
                },
                OwnerReferences = string.IsNullOrWhiteSpace(_environment.PodUid)
                    ? null
                    : [new V1OwnerReference { ApiVersion = "v1", Kind = "Pod", Name = pod, Uid = _environment.PodUid }],
            },
            Spec = new V1LeaseSpec
            {
                HolderIdentity = key,
                AcquireTime = DateTime.UtcNow,
                RenewTime = DateTime.UtcNow,
            },
        };

        try
        {
            await client.CoordinationV1.CreateNamespacedLeaseAsync(lease, ns, fieldManager: fieldManager, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // The pod published before, from an earlier start of its container.
        }

        string now = Now();
        var patch = new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["annotations"] = new JsonObject
                {
                    [KubernetesProtocol.ManifestAnnotation] = manifest,
                    [KubernetesProtocol.InstanceKeyAnnotation] = key,
                    [KubernetesProtocol.PublicationAnnotation] = null,
                    [KubernetesProtocol.PublicationKeyAnnotation] = null,
                    [KubernetesProtocol.RefusalAnnotation] = null,
                    [KubernetesProtocol.RefusalKeyAnnotation] = null,
                },
            },
            ["spec"] = new JsonObject
            {
                ["holderIdentity"] = key,
                ["acquireTime"] = now,
                ["renewTime"] = now,
            },
        };
        await client.CoordinationV1.PatchNamespacedLeaseAsync(new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch), pod, ns, fieldManager: fieldManager, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // The gateway answers on the Lease; nothing else tells the instance a gateway watches it, so an
    // answer that never comes fails the start, as a refused one does.
    private async Task<ApplicationInstancePublication> WaitForAnswerAsync(IKubernetes client, string ns, string pod, string key, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_answerTimeout);
        try
        {
            while (true)
            {
                V1Lease? lease = await ReadLeaseAsync(client, ns, pod, timeout.Token).ConfigureAwait(false);
                if (lease is not null)
                {
                    if (Annotation(lease, KubernetesProtocol.RefusalKeyAnnotation) == key)
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PublicationRefused, Annotation(lease, KubernetesProtocol.RefusalAnnotation)));
                    }

                    if (Annotation(lease, KubernetesProtocol.PublicationKeyAnnotation) == key
                        && Annotation(lease, KubernetesProtocol.PublicationAnnotation) is { } answer)
                    {
                        return _json.Deserialize<ApplicationInstancePublication>(Encoding.UTF8.GetBytes(answer))
                            ?? throw new InvalidOperationException(ErrorMessages.PublicationEmpty);
                    }
                }

                await Task.Delay(_answerPollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PublicationUnanswered, ns, _answerTimeout));
        }
    }

    private static async Task<V1Lease?> ReadLeaseAsync(IKubernetes client, string ns, string pod, CancellationToken cancellationToken)
    {
        try
        {
            return await client.CoordinationV1.ReadNamespacedLeaseAsync(pod, ns, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private (string Namespace, string Pod) ResolvePod()
    {
        if (!KubernetesProtocol.TryParseGatewayEndpoint(_environment.GatewayEndpoint is { } endpoint && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? uri : null, out string? ns))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.GatewayEndpointInvalid, AppEnvironment.Variables.ManifestEndpoint, _environment.GatewayEndpoint));
        }

        if (string.IsNullOrWhiteSpace(_environment.PodName))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PodNameMissing, KubernetesProtocol.PodNameVariable));
        }

        return (ns, _environment.PodName);
    }

    private IKubernetes GetClient()
    {
        lock (_lock)
        {
            return _client ??= new k8s.Kubernetes(ResolveConfiguration());
        }
    }

    // In a pod, its service account; outside one, as a developer runs the program against a cluster,
    // the kubeconfig kubectl would use. The client library's own last resort, an unauthenticated
    // http://localhost:8080, is never what an instance means, so its absence fails the publication.
    private static KubernetesClientConfiguration ResolveConfiguration()
    {
        if (KubernetesClientConfiguration.IsInCluster())
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }

        if (Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } variable)
        {
            foreach (string candidate in variable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(candidate))
                {
                    return KubernetesClientConfiguration.BuildConfigFromConfigFile(candidate);
                }
            }
        }

        if (File.Exists(KubernetesClientConfiguration.KubeConfigDefaultLocation))
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(KubernetesClientConfiguration.KubeConfigDefaultLocation);
        }

        throw new InvalidOperationException(ErrorMessages.ClusterUnavailable);
    }

    private static string? Annotation(V1Lease lease, string name)
    {
        return lease.Metadata?.Annotations is { } annotations && annotations.TryGetValue(name, out string? value) ? value : null;
    }

    // The API server's MicroTime: RFC 3339 with microseconds.
    private static string Now()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }
}
