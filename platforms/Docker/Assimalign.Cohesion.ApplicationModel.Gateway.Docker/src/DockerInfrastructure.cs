using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Docker;
using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Runs a gateway's resources as containers on a Docker-compatible engine, Podman's included: each
/// resource is one container per replica of the image bound to it, on the gateway's network under the
/// resource's name. It is the gateway half of the Docker platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddImage"/> binds a resource to its container image. Deploying the resource creates its
/// containers, and each receives the runtime contract as its environment: the application's and the
/// resource's names, the <c>docker</c> platform, the network its gateway runs, the deployment's id, its
/// <c>http</c> endpoint on <see cref="DockerInfrastructureOptions.EndpointPort"/>, its own container's
/// name, and every setting as a configuration variable. The engine publishes each container's
/// <c>http</c> and control-plane ports on loopback ports of its host.
/// </para>
/// <para>
/// The infrastructure is also the gateway's inbound channel and its instance client:
/// <see cref="DockerApplicationGatewayBuilderExtensions.UseDocker"/> sets it as both. The gateway
/// pulls: it polls each instance container's published control-plane port for the instance's
/// publication and renewals, answers there, and calls the instance's control plane over the same
/// port. An instance opts into the platform with the instance half's <c>UseDocker()</c>.
/// </para>
/// <para>
/// Workloads outlive the gateway, as a platform's do: a gateway that stops leaves its containers
/// running, and one that runs again over the same network adopts them. A resource whose image, ports,
/// replica count, or settings changed is replaced with new containers, and tearing a resource down
/// removes its containers. The network stays.
/// </para>
/// </remarks>
[DebuggerDisplay("{Network} at {Endpoint}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class DockerInfrastructure : IApplicationGatewayInfrastructureProvider, IApplicationGatewayInstanceClient, IAsyncDisposable
{
    private static readonly TimeSpan _watchInterval = TimeSpan.FromSeconds(1);
    private const int failureOutputLines = 20;
    private static readonly IReadOnlyDictionary<OutputName, string> _noOutputs = new Dictionary<OutputName, string>();

    private readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), string> _images = new();
    private readonly DockerInfrastructureOptions _options;
    private readonly IDockerEngine _engine;
    private readonly string _host;
    private readonly HttpClient _http;
    private readonly DockerChannel _channel;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private int _isDisposed;

    // The host is where the gateway reaches the ports the engine publishes: the engine's loopback.
    internal DockerInfrastructure(DockerInfrastructureOptions options, IDockerEngine engine, string endpoint, string host = DockerWorkload.PublishAddress)
    {
        _options = options;
        _engine = engine;
        _host = host;
        Endpoint = endpoint;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1) });
        _channel = new DockerChannel(engine, options.Network, options.ControlPlanePort, host, _http);
    }

    /// <summary>
    /// Gets the platform the infrastructure deploys onto: <c>docker</c>. A resource's extensions for it
    /// apply here, and its programs select their Docker client by it.
    /// </summary>
    public PlatformName Platform => DockerProtocol.PlatformName;

    /// <summary>
    /// Gets the network the gateway runs its resources on.
    /// </summary>
    public string Network => _options.Network;

    /// <summary>
    /// Gets the engine's API endpoint the infrastructure resolved, such as
    /// <c>npipe:////./pipe/docker_engine</c>.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Creates the infrastructure for the engine the options name.
    /// </summary>
    /// <param name="options">
    /// The engine and the shape of what runs there; <see langword="null"/> uses the defaults. They are
    /// copied: changing them later changes nothing.
    /// </param>
    /// <returns>The infrastructure. It reaches the engine only when the gateway first needs it.</returns>
    /// <exception cref="ArgumentException">
    /// The network is not a DNS label, a port is out of range, the image pull policy is unknown, or the
    /// endpoint is not of a form the docker CLI accepts.
    /// </exception>
    /// <exception cref="NotSupportedException">The endpoint asks for TLS or SSH, which the platform does not speak yet.</exception>
    public static DockerInfrastructure Create(DockerInfrastructureOptions? options = null)
    {
        DockerInfrastructureOptions copy = Validate(options);
        DockerEngineEndpoint endpoint = DockerEngineEndpoint.Resolve(copy.Endpoint);
        return new DockerInfrastructure(copy, new DockerEngineClient(endpoint), endpoint.Value);
    }

    /// <summary>
    /// Binds a resource to the container image that runs it, such as <c>cohesion-docker-web:e2e</c>.
    /// </summary>
    /// <remarks>
    /// The image is a resource's program built as a container: an ordinary application that opts its
    /// resource into the Docker platform. It must be built for the engine's architecture.
    /// </remarks>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="image">The image reference the engine runs, and pulls when it has none by that reference.</param>
    /// <returns>This infrastructure.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, the image is
    /// empty, or the resource already has an image.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The infrastructure is disposed.</exception>
    public DockerInfrastructure AddImage(ProviderName provider, ResourceName name, string image)
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
    /// Stops polling the network's instances and releases the connection to the engine. What the
    /// gateway deployed keeps running.
    /// </summary>
    /// <returns>A task that completes when the channel has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        await _channel.StopAsync().ConfigureAwait(false);
        _http.Dispose();
        _engine.Dispose();
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
        string revision = DockerWorkload.GetRevision(GetImage(resource), _options.EndpointPort, _options.ControlPlanePort);
        string workload = DockerWorkload.GetWorkload(revision, resource, settings);
        IReadOnlyList<DockerContainerSummary> running = await _engine.ListContainersAsync(DockerWorkload.SelectResource(Network, resource), cancellationToken).ConfigureAwait(false);

        // The containers decide, as the platform runs them: none is a create, all of the workload and as
        // many as it runs is nothing, and anything else is an update.
        ApplicationGatewayDeploymentChange change = running.Count == 0
            ? ApplicationGatewayDeploymentChange.Create
            : running.Count == DockerWorkload.GetReplicas(resource) && running.All(container => Label(container, DockerWorkload.WorkloadLabel) == workload)
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
        string revision = DockerWorkload.GetRevision(image, _options.EndpointPort, _options.ControlPlanePort);
        string workload = DockerWorkload.GetWorkload(revision, resource, settings);
        int replicas = DockerWorkload.GetReplicas(resource);
        await EnsureNetworkAsync(cancellationToken).ConfigureAwait(false);
        await EnsureImageAsync(resource, image, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DockerContainerSummary> previous = await _engine.ListContainersAsync(DockerWorkload.SelectResource(Network, resource), cancellationToken).ConfigureAwait(false);
        var current = new HashSet<string>(StringComparer.Ordinal);
        for (int replica = 0; replica < replicas; replica++)
        {
            string name = DockerWorkload.GetContainerName(Network, resource, workload, replica);
            current.Add(name);

            // A container of this name from an attempt that did not finish is replaced, unless it is
            // this deployment's already.
            DockerContainerSummary? existing = previous.FirstOrDefault(container => DockerWorkload.GetName(container) == name);
            if (existing is not null && Label(existing, DockerWorkload.DeploymentLabel) == deployment.ToString())
            {
                await _engine.StartContainerAsync(name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (existing is not null || await _engine.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false) is not null)
            {
                await _engine.RemoveContainerAsync(name, cancellationToken).ConfigureAwait(false);
            }

            DockerContainerCreateRequest container = DockerWorkload.CreateContainer(model, resource, settings, deployment, image, revision, workload, replica, name, Network, _options);
            await _engine.CreateContainerAsync(name, container, cancellationToken).ConfigureAwait(false);
            await _engine.StartContainerAsync(name, cancellationToken).ConfigureAwait(false);
        }

        // The containers this deployment replaces go once its own run, as a rollout replaces the
        // previous one's pods.
        foreach (DockerContainerSummary container in previous)
        {
            if (DockerWorkload.GetName(container) is { } name && !current.Contains(name))
            {
                await _engine.RemoveContainerAsync(container.Id ?? name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.WatchResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, DeploymentId deployment, CancellationToken cancellationToken)
    {
        // A container of the deployment that ended, or keeps restarting, fails the step with the
        // engine's reason. A deployment whose containers a later one replaced, or a teardown removed,
        // has ended.
        Dictionary<string, string> selector = DockerWorkload.SelectResource(Network, resource);
        selector[DockerWorkload.DeploymentLabel] = deployment.ToString();
        while (true)
        {
            IReadOnlyList<DockerContainerSummary> containers = await _engine.ListContainersAsync(selector, cancellationToken).ConfigureAwait(false);
            if (containers.Count == 0)
            {
                return;
            }

            foreach (DockerContainerSummary summary in containers)
            {
                string id = summary.Id ?? DockerWorkload.GetName(summary) ?? string.Empty;
                if (await _engine.InspectContainerAsync(id, cancellationToken).ConfigureAwait(false) is { } container
                    && DockerWorkload.GetFailure(container) is { } reason)
                {
                    string message = Format(ErrorMessages.WorkloadFailed, (container.Name ?? DockerWorkload.GetName(summary))?.TrimStart('/'), resource.Provider, resource.Name, reason);
                    throw new InvalidOperationException(await ReadOutputAsync(id, cancellationToken).ConfigureAwait(false) is { Length: > 0 } output
                        ? Format(ErrorMessages.WorkloadOutput, message, Environment.NewLine, output)
                        : message);
                }
            }

            await Task.Delay(_watchInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask<IReadOnlyDictionary<OutputName, string>> IApplicationGatewayInfrastructureProvider.GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainerSummary> containers = await _engine.ListContainersAsync(DockerWorkload.SelectResource(Network, resource), cancellationToken).ConfigureAwait(false);
        return containers.Count == 0
            ? _noOutputs
            : new Dictionary<OutputName, string> { [OutputName.Endpoint] = DockerWorkload.GetEndpoint(resource, _options) };
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainerSummary> containers = await _engine.ListContainersAsync(DockerWorkload.SelectResource(Network, resource), cancellationToken).ConfigureAwait(false);
        foreach (DockerContainerSummary container in containers)
        {
            if ((container.Id ?? DockerWorkload.GetName(container)) is { } id)
            {
                await _engine.RemoveContainerAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The channel polls the network's instance containers and hands what they publish to the
    // gateway's publisher, on the gateway's own flow, until the gateway's run ends.
    async ValueTask IApplicationGatewayInfrastructureProvider.StartAsync(IApplicationGatewayContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        await EnsureNetworkAsync(cancellationToken).ConfigureAwait(false);
        _channel.Start(context.Publisher);
    }

    async ValueTask IApplicationGatewayInfrastructureProvider.StopAsync(CancellationToken cancellationToken)
    {
        await _channel.StopAsync().ConfigureAwait(false);
    }

    // The gateway calls an instance's control plane on the host port the engine published its
    // control-plane port on. The content crosses as bytes, which the control plane reads with its own
    // serializer.
    async ValueTask<ApplicationActionResult> IApplicationGatewayInstanceClient.ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(context);

        bool write = context.Operation == ApplicationActionOperation.Write;
        Uri address = await ResolveAsync(instance, string.Concat("actions/", Uri.EscapeDataString(action.Value)), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(write ? HttpMethod.Post : HttpMethod.Get, address);
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

        Uri address = await ResolveAsync(instance, string.Concat("state/", Uri.EscapeDataString(snapshot.Action.Value)), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Put, address)
        {
            Content = CreateContent(snapshot.Value, _json.MediaType),
        };

        return await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);
    }

    // The control plane answers every outcome with a result: 200, 400, 404, or 405. Anything else, or a
    // body that is not a result, such as a 503 from an instance whose control plane is not open yet, is
    // no answer.
    private async Task<ApplicationActionResult> SendAsync(ApplicationInstanceManifest instance, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_json.MediaType));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    // The manifest names the container and its port inside it; the engine says where it published that
    // port on its host, which can change when the container restarts.
    private async Task<Uri> ResolveAsync(ApplicationInstanceManifest instance, string route, CancellationToken cancellationToken)
    {
        if (!DockerProtocol.TryParseControlPlaneEndpoint(instance.ControlPlaneEndpoint, out _, out string? container, out int port))
        {
            throw new ArgumentException(Format(ErrorMessages.ControlPlaneEndpointUnsupported, instance.ControlPlaneEndpoint), nameof(instance));
        }

        DockerContainerInspectResponse? inspected;
        try
        {
            inspected = await _engine.InspectContainerAsync(container, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerEngineException exception)
        {
            throw new HttpRequestException(Format(ErrorMessages.ControlPlaneUnpublished, container, port), exception);
        }

        if (inspected is null || DockerWorkload.GetHostPort(inspected, port) is not { } published)
        {
            throw new HttpRequestException(Format(ErrorMessages.ControlPlaneUnpublished, container, port));
        }

        return new Uri(string.Concat("http://", _host, ":", published.ToString(CultureInfo.InvariantCulture), "/", route));
    }

    // What the program wrote last says why it ended, such as its unhandled exception; reading it is
    // best effort, and a failure to read leaves the engine's reason alone.
    private async Task<string?> ReadOutputAsync(string container, CancellationToken cancellationToken)
    {
        try
        {
            return (await _engine.ReadLogsAsync(container, failureOutputLines, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (DockerEngineException)
        {
            return null;
        }
    }

    private async Task EnsureNetworkAsync(CancellationToken cancellationToken)
    {
        if (await _engine.InspectNetworkAsync(Network, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        await _engine.CreateNetworkAsync(new DockerNetworkCreateRequest
        {
            Name = Network,
            Driver = "bridge",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [DockerWorkload.ManagedByLabel] = DockerWorkload.ManagedByValue },
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureImageAsync(ApplicationResourceManifest resource, string image, CancellationToken cancellationToken)
    {
        switch (_options.ImagePullPolicy)
        {
            case "Always":
                await _engine.PullImageAsync(image, cancellationToken).ConfigureAwait(false);
                return;

            case "Never":
                if (await _engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new InvalidOperationException(Format(ErrorMessages.ImageMissingLocally, image, resource.Provider, resource.Name));
                }

                return;

            default:
                if (await _engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false) is null)
                {
                    await _engine.PullImageAsync(image, cancellationToken).ConfigureAwait(false);
                }

                return;
        }
    }

    private string GetImage(ApplicationResourceManifest resource)
    {
        return _images.TryGetValue((resource.Provider, resource.Name), out string? image)
            ? image
            : throw new InvalidOperationException(Format(ErrorMessages.ImageUnbound, resource.Provider, resource.Name));
    }

    private static DockerInfrastructureOptions Validate(DockerInfrastructureOptions? options)
    {
        options ??= new DockerInfrastructureOptions();
        var copy = new DockerInfrastructureOptions
        {
            Endpoint = options.Endpoint,
            Network = options.Network,
            EndpointPort = options.EndpointPort,
            ControlPlanePort = options.ControlPlanePort,
            ImagePullPolicy = options.ImagePullPolicy,
        };

        if (!IsDnsLabel(copy.Network))
        {
            throw new ArgumentException(Format(ErrorMessages.NetworkInvalid, copy.Network), nameof(options));
        }

        if (copy.EndpointPort is <= 0 or > 65535 || copy.ControlPlanePort is <= 0 or > 65535 || copy.EndpointPort == copy.ControlPlanePort)
        {
            throw new ArgumentException(ErrorMessages.PortInvalid, nameof(options));
        }

        if (copy.ImagePullPolicy is not ("IfNotPresent" or "Always" or "Never"))
        {
            throw new ArgumentException(Format(ErrorMessages.ImagePullPolicyInvalid, copy.ImagePullPolicy), nameof(options));
        }

        return copy;
    }

    /// <summary>
    /// Creates the infrastructure over another engine, for tests.
    /// </summary>
    internal static DockerInfrastructure Create(DockerInfrastructureOptions? options, IDockerEngine engine, string host)
    {
        return new DockerInfrastructure(Validate(options), engine, "test://engine", host);
    }

    private static string? Label(DockerContainerSummary container, string name)
    {
        return container.Labels is { } labels && labels.TryGetValue(name, out string? value) ? value : null;
    }

    private static ReadOnlyMemoryContent CreateContent(ReadOnlyMemory<byte> body, string mediaType)
    {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static HttpRequestException Unanswered(ApplicationInstanceManifest instance, HttpStatusCode status, Exception? inner)
    {
        return new HttpRequestException(Format(ErrorMessages.InstanceUnanswered, instance.Key, instance.ControlPlaneEndpoint, (int)status), inner, status);
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
        private readonly DockerInfrastructure _infrastructure;

        public DebugView(DockerInfrastructure infrastructure)
        {
            _infrastructure = infrastructure;
        }

        public PlatformName Platform => _infrastructure.Platform;

        public string Network => _infrastructure.Network;

        public string Endpoint => _infrastructure.Endpoint;
    }
}
