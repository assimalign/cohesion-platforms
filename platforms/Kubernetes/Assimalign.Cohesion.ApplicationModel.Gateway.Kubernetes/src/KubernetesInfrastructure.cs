using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;
using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Runs a gateway's resources on a Kubernetes cluster: each resource is a Deployment of the image bound
/// to it, behind a Service, in the gateway's namespace. It is the gateway half of the Kubernetes
/// platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddImage"/> binds a resource to its container image. Deploying the resource applies its
/// Deployment and Service with server-side apply. Each pod receives the runtime contract as its
/// environment: the application's and the resource's names, the <c>kubernetes</c> platform, the
/// namespace its gateway watches, the deployment's id, its <c>http</c> endpoint on
/// <see cref="KubernetesInfrastructureOptions.EndpointPort"/>, its own pod through the downward API, and
/// every setting as a configuration variable. Every instance runs under the namespace's instance
/// service account, which may manage Leases only.
/// </para>
/// <para>
/// The infrastructure is also the gateway's inbound channel and its instance client, all through the
/// cluster's API server, so the gateway runs inside the cluster or outside it alike:
/// <see cref="KubernetesApplicationGatewayBuilderExtensions.UseKubernetes"/> sets it as both the
/// gateway's infrastructure and its instance client. An instance
/// opts into the platform with the instance half's <c>UseKubernetes()</c>: it publishes and renews
/// through a Lease named after its pod, and the gateway calls its control plane through the API
/// server's pod proxy.
/// </para>
/// <para>
/// Workloads outlive the gateway, as a platform's do: a gateway that stops leaves them running, and one
/// that runs again over the same namespace adopts them. A resource whose image, ports, replica count, or
/// settings changed is updated, and tearing a resource down deletes its Deployment and Service.
/// </para>
/// </remarks>
[DebuggerDisplay("{Namespace}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class KubernetesInfrastructure : IApplicationGatewayInfrastructureProvider, IApplicationGatewayInstanceClient, IAsyncDisposable
{
    private const string fieldManager = "cohesion-gateway";

    private static readonly TimeSpan _watchInterval = TimeSpan.FromSeconds(2);

    // Waiting reasons a pod does not recover from without a change to what it runs.
    private static readonly HashSet<string> _failedReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff",
        "ImagePullBackOff",
        "InvalidImageName",
        "CreateContainerConfigError",
        "CreateContainerError",
    };

    private static readonly IReadOnlyDictionary<OutputName, string> _noOutputs = new Dictionary<OutputName, string>();

    private readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), string> _images = new();
    private readonly KubernetesInfrastructureOptions _options;
    private readonly k8s.Kubernetes _client;
    private readonly HttpClient _proxy;
    private readonly Uri _apiServer;
    private readonly KubernetesLeaseChannel _channel;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private int _isDisposed;

    private KubernetesInfrastructure(KubernetesInfrastructureOptions options, KubernetesClientConfiguration configuration)
    {
        _options = options;

        // The pipeline handler sits ahead of the client's own transport, which carries its credentials,
        // such as a kubeconfig's client certificate; a bearer token is applied per request.
        var pipeline = new KubernetesPipelineHandler();
        _client = new k8s.Kubernetes(configuration, pipeline);
        _proxy = new HttpClient(pipeline, disposeHandler: false);
        _apiServer = new Uri(_client.BaseUri.AbsoluteUri.TrimEnd('/') + "/");
        _channel = new KubernetesLeaseChannel(_client, options.Namespace);
    }

    /// <summary>
    /// Gets the platform the infrastructure deploys onto: <c>kubernetes</c>. A resource's extensions for
    /// it apply here, and its programs select their Kubernetes client by it.
    /// </summary>
    public PlatformName Platform => KubernetesProtocol.PlatformName;

    /// <summary>
    /// Gets the namespace the gateway runs its resources in.
    /// </summary>
    public string Namespace => _options.Namespace;

    /// <summary>
    /// Creates the infrastructure for the cluster the options name.
    /// </summary>
    /// <param name="options">
    /// The cluster and the shape of what runs there; <see langword="null"/> uses the defaults. They are
    /// copied: changing them later changes nothing.
    /// </param>
    /// <returns>The infrastructure.</returns>
    /// <exception cref="ArgumentException">The namespace is not a DNS label, or a port is out of range.</exception>
    /// <exception cref="FileNotFoundException">The kubeconfig file the options name does not exist.</exception>
    /// <exception cref="InvalidOperationException">No cluster configuration could be resolved.</exception>
    public static KubernetesInfrastructure Create(KubernetesInfrastructureOptions? options = null)
    {
        options ??= new KubernetesInfrastructureOptions();
        var copy = new KubernetesInfrastructureOptions
        {
            KubeConfigPath = options.KubeConfigPath,
            Context = options.Context,
            Namespace = options.Namespace,
            EndpointPort = options.EndpointPort,
            ControlPlanePort = options.ControlPlanePort,
            ImagePullPolicy = options.ImagePullPolicy,
        };

        if (!IsDnsLabel(copy.Namespace))
        {
            throw new ArgumentException(Format(ErrorMessages.NamespaceInvalid, copy.Namespace), nameof(options));
        }

        if (copy.EndpointPort is <= 0 or > 65535 || copy.ControlPlanePort is <= 0 or > 65535)
        {
            throw new ArgumentException(ErrorMessages.PortInvalid, nameof(options));
        }

        return new KubernetesInfrastructure(copy, ResolveConfiguration(copy));
    }

    /// <summary>
    /// Binds a resource to the container image that runs it, such as
    /// <c>localhost:5001/orders-api:1.0</c>.
    /// </summary>
    /// <remarks>
    /// The image is a resource's program built as a container: an ordinary application that opts its
    /// resource into the Kubernetes platform.
    /// </remarks>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="image">The image reference the cluster pulls.</param>
    /// <returns>This infrastructure.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, the image is
    /// empty, or the resource already has an image.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The infrastructure is disposed.</exception>
    public KubernetesInfrastructure AddImage(ProviderName provider, ResourceName name, string image)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            throw new ArgumentException(ErrorMessages.ImageMissing, nameof(image));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        if (!_images.TryAdd((provider, name), image))
        {
            throw new ArgumentException(Format(ErrorMessages.ImageDuplicate, provider, name), nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Stops receiving the namespace's publications and releases the connection to the cluster. What
    /// the gateway deployed keeps running.
    /// </summary>
    /// <returns>A task that completes when the channel has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        await _channel.StopAsync().ConfigureAwait(false);
        _proxy.Dispose();
        _client.Dispose();
    }

    IReadOnlyCollection<IApplicationGatewayDeploymentAction> IApplicationGatewayInfrastructureProvider.GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        return [];
    }

    async ValueTask<ApplicationGatewayDeploymentPlan> IApplicationGatewayInfrastructureProvider.PlanResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        CancellationToken cancellationToken)
    {
        string revision = KubernetesWorkload.GetRevision(resource, settings, GetImage(resource), _options.EndpointPort, _options.ControlPlanePort);
        V1Deployment? running = await ReadAsync(() => _client.AppsV1.ReadNamespacedDeploymentAsync(resource.Name.Value, Namespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
        ApplicationGatewayDeploymentChange change = running is null
            ? ApplicationGatewayDeploymentChange.Create
            : running.Metadata?.Annotations?.TryGetValue(KubernetesWorkload.RevisionAnnotation, out string? current) == true && current == revision
                ? ApplicationGatewayDeploymentChange.None
                : ApplicationGatewayDeploymentChange.Update;

        return new ApplicationGatewayDeploymentPlan(change, revision);
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.DeployResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        string image = GetImage(resource);
        string revision = KubernetesWorkload.GetRevision(resource, settings, image, _options.EndpointPort, _options.ControlPlanePort);
        await EnsureNamespaceAsync(cancellationToken).ConfigureAwait(false);

        (V1ServiceAccount account, V1Role role, V1RoleBinding binding) = KubernetesWorkload.CreateInstanceAccess(Namespace);
        await _client.CoreV1.PatchNamespacedServiceAccountAsync(Apply(account), account.Metadata.Name, Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _client.RbacAuthorizationV1.PatchNamespacedRoleAsync(Apply(role), role.Metadata.Name, Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _client.RbacAuthorizationV1.PatchNamespacedRoleBindingAsync(Apply(binding), binding.Metadata.Name, Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        V1Deployment workload = KubernetesWorkload.CreateDeployment(model, resource, settings, deployment, image, revision, Namespace, _options);
        await _client.AppsV1.PatchNamespacedDeploymentAsync(Apply(workload), workload.Metadata.Name, Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        V1Service service = KubernetesWorkload.CreateService(resource, Namespace, _options);
        await _client.CoreV1.PatchNamespacedServiceAsync(Apply(service), service.Metadata.Name, Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.WatchResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, DeploymentId deployment, CancellationToken cancellationToken)
    {
        // A pod of the deployment that cannot run without a change to what it runs fails the step with
        // the platform's reason. A deployment that a later one replaced, or a teardown removed, has ended.
        string selector = string.Concat(KubernetesWorkload.NameLabel, "=", resource.Name.Value, ",", KubernetesWorkload.DeploymentLabel, "=", deployment.ToString());
        while (true)
        {
            V1Deployment? running = await ReadAsync(() => _client.AppsV1.ReadNamespacedDeploymentAsync(resource.Name.Value, Namespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (running?.Spec?.Template?.Metadata?.Labels?.TryGetValue(KubernetesWorkload.DeploymentLabel, out string? current) != true || current != deployment.ToString())
            {
                return;
            }

            V1PodList pods = await _client.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: selector, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (V1Pod pod in pods.Items)
            {
                foreach (V1ContainerStatus status in pod.Status?.ContainerStatuses ?? [])
                {
                    if (status.State?.Waiting is { Reason: { } reason } waiting && _failedReasons.Contains(reason))
                    {
                        throw new InvalidOperationException(Format(ErrorMessages.WorkloadFailed, pod.Metadata?.Name, resource.Provider, resource.Name, reason, waiting.Message));
                    }
                }
            }

            await Task.Delay(_watchInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask<IReadOnlyDictionary<OutputName, string>> IApplicationGatewayInfrastructureProvider.GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        V1Service? service = await ReadAsync(() => _client.CoreV1.ReadNamespacedServiceAsync(resource.Name.Value, Namespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return service is null
            ? _noOutputs
            : new Dictionary<OutputName, string> { [OutputName.Endpoint] = KubernetesWorkload.GetEndpoint(resource, Namespace) };
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        await IgnoreMissingAsync(() => _client.AppsV1.DeleteNamespacedDeploymentAsync(resource.Name.Value, Namespace, propagationPolicy: "Background", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await IgnoreMissingAsync(() => _client.CoreV1.DeleteNamespacedServiceAsync(resource.Name.Value, Namespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // The channel watches the namespace's instance Leases and hands what they carry to the gateway's
    // publisher, on the gateway's own flow, until the gateway's run ends.
    async ValueTask IApplicationGatewayInfrastructureProvider.StartAsync(IApplicationGatewayContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        await EnsureNamespaceAsync(cancellationToken).ConfigureAwait(false);
        _channel.Start(context.Publisher);
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.StopAsync(CancellationToken cancellationToken)
    {
        await _channel.StopAsync().ConfigureAwait(false);
    }

    // The gateway calls an instance's control plane through the API server's pod proxy, at the port its
    // manifest names. The content crosses as bytes, which the control plane reads with its own serializer.
    async ValueTask<ApplicationActionResult> IApplicationGatewayInstanceClient.ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(context);

        bool write = context.Operation == ApplicationActionOperation.Write;
        using var request = new HttpRequestMessage(write ? HttpMethod.Post : HttpMethod.Get, ResolveProxy(instance, string.Concat("actions/", Uri.EscapeDataString(action.Value))));
        if (write)
        {
            request.Content = CreateContent(context.Input, context.Serializer.MediaType);
        }

        return await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<ApplicationActionResult> IApplicationGatewayInstanceClient.RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Put, ResolveProxy(instance, string.Concat("state/", Uri.EscapeDataString(snapshot.Action.Value))))
        {
            Content = CreateContent(snapshot.Value, _json.MediaType),
        };

        return await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);
    }

    // The control plane answers every outcome with a result: 200, 400, 404, or 405. Anything else, or a
    // body that is not a result, such as the API server's own status for a pod that is gone, is no answer.
    private async Task<ApplicationActionResult> SendAsync(ApplicationInstanceManifest instance, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_json.MediaType));
        if (_client.Credentials is { } credentials)
        {
            await credentials.ProcessHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using HttpResponseMessage response = await _proxy.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (body.Length > 0 && _json.Deserialize<ApplicationActionResult>(body) is { } result)
                {
                    return result;
                }
            }
            catch (ApplicationContentException exception)
            {
                throw Unanswered(instance, response.StatusCode, exception);
            }
        }

        throw Unanswered(instance, response.StatusCode, inner: null);
    }

    private Uri ResolveProxy(ApplicationInstanceManifest instance, string route)
    {
        if (!KubernetesProtocol.TryParseControlPlaneEndpoint(instance.ControlPlaneEndpoint, out string? ns, out string? pod, out int port))
        {
            throw new ArgumentException(Format(ErrorMessages.ControlPlaneEndpointUnsupported, instance.ControlPlaneEndpoint), nameof(instance));
        }

        return new Uri(_apiServer, string.Concat(
            "api/v1/namespaces/", Uri.EscapeDataString(ns),
            "/pods/", Uri.EscapeDataString(pod), ":", port.ToString(CultureInfo.InvariantCulture),
            "/proxy/", route));
    }

    private async Task EnsureNamespaceAsync(CancellationToken cancellationToken)
    {
        var ns = new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = Namespace,
                Labels = new Dictionary<string, string> { [KubernetesProtocol.ManagedByLabel] = KubernetesProtocol.ManagedByValue },
            },
        };
        await _client.CoreV1.PatchNamespaceAsync(Apply(ns), Namespace, fieldManager: fieldManager, force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string GetImage(ApplicationResourceManifest resource)
    {
        return _images.TryGetValue((resource.Provider, resource.Name), out string? image)
            ? image
            : throw new InvalidOperationException(Format(ErrorMessages.ImageUnbound, resource.Provider, resource.Name));
    }

    private static V1Patch Apply<T>(T value)
    {
        return new V1Patch(KubernetesJson.Serialize(value), V1Patch.PatchType.ApplyPatch);
    }

    private static ReadOnlyMemoryContent CreateContent(ReadOnlyMemory<byte> body, string mediaType)
    {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static async Task<T?> ReadAsync<T>(Func<Task<T>> read) where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task IgnoreMissingAsync<T>(Func<Task<T>> delete)
    {
        try
        {
            await delete().ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    private static HttpRequestException Unanswered(ApplicationInstanceManifest instance, HttpStatusCode status, Exception? inner)
    {
        return new HttpRequestException(Format(ErrorMessages.InstanceUnanswered, instance.Key, instance.ControlPlaneEndpoint, (int)status), inner, status);
    }

    // Resolved as kubectl resolves it: the named kubeconfig, KUBECONFIG, the default kubeconfig, then
    // the pod's service account when the gateway runs in the cluster.
    private static KubernetesClientConfiguration ResolveConfiguration(KubernetesInfrastructureOptions options)
    {
        if (options.KubeConfigPath is { } path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(path, path);
            }

            return KubernetesClientConfiguration.BuildConfigFromConfigFile(path, options.Context);
        }

        if (Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } variable)
        {
            foreach (string candidate in variable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(candidate))
                {
                    return KubernetesClientConfiguration.BuildConfigFromConfigFile(candidate, options.Context);
                }
            }
        }

        if (File.Exists(KubernetesClientConfiguration.KubeConfigDefaultLocation))
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(KubernetesClientConfiguration.KubeConfigDefaultLocation, options.Context);
        }

        return KubernetesClientConfiguration.InClusterConfig();
    }

    private static bool IsDnsLabel(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly KubernetesInfrastructure _infrastructure;

        public DebugView(KubernetesInfrastructure infrastructure)
        {
            _infrastructure = infrastructure;
        }

        public PlatformName Platform => _infrastructure.Platform;

        public string Namespace => _infrastructure.Namespace;
    }
}
