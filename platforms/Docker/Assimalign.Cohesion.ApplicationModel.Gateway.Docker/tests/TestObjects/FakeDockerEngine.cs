using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

internal sealed class FakeDockerEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _requests = [];
    private readonly List<HttpListenerResponse> _eventStreams = [];
    private readonly List<DockerEngineRequest> _recorded = [];
    private readonly Dictionary<string, DockerImageInspectResponse> _images =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworkState> _networks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VolumeState> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContainerState> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _execs = new(StringComparer.Ordinal);
    private readonly Queue<int> _execExitCodes = new();
    private readonly Task _acceptLoop;

    private int _networkSequence;
    private int _containerSequence;
    private int _execSequence;

    public FakeDockerEngine()
    {
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        Endpoint = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public Uri Endpoint { get; }

    public string ApiVersion { get; set; } = "1.51";

    public string MinimumApiVersion { get; set; } = "1.25";

    public HttpStatusCode? NetworkCreateFailure { get; set; }

    public string? ImageAvailableAfterLoad { get; set; }

    public string ImageIdAfterLoad { get; set; } = $"sha256:{new string('c', 64)}";

    public string[]? RepoDigestsAfterLoad { get; set; }

    public byte[]? LoadedArchive { get; private set; }

    public IReadOnlyList<DockerEngineRequest> RecordedRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _recorded];
            }
        }
    }

    public int NetworkCount
    {
        get
        {
            lock (_gate)
            {
                return _networks.Count;
            }
        }
    }

    public int VolumeCount
    {
        get
        {
            lock (_gate)
            {
                return _volumes.Count;
            }
        }
    }

    public int ContainerCount
    {
        get
        {
            lock (_gate)
            {
                return _containers.Count;
            }
        }
    }

    public void AddImage(
        string reference,
        string imageId,
        params string[] repoDigests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        lock (_gate)
        {
            _images[reference] = new DockerImageInspectResponse
            {
                Id = imageId,
                RepoDigests = repoDigests.Length == 0 ? [reference] : repoDigests,
            };
        }
    }

    public string SeedContainer(
        string name,
        DockerContainerCreateRequest request,
        bool running = true,
        int exitCode = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            string id = $"container-{++_containerSequence}";
            _containers[name] = new ContainerState(id, name, request)
            {
                Running = running,
                ExitCode = exitCode,
            };
            return id;
        }
    }

    public void SetContainerState(
        string idOrName,
        bool running,
        int exitCode = 0)
    {
        lock (_gate)
        {
            ContainerState container = FindContainer(idOrName)
                ?? throw new InvalidOperationException($"Container '{idOrName}' is not present.");
            container.Running = running;
            container.ExitCode = exitCode;
        }
    }

    public void QueueExecExitCodes(params int[] exitCodes)
    {
        ArgumentNullException.ThrowIfNull(exitCodes);
        lock (_gate)
        {
            for (int index = 0; index < exitCodes.Length; index++)
            {
                _execExitCodes.Enqueue(exitCodes[index]);
            }
        }
    }

    public void ClearRecordedRequests()
    {
        lock (_gate)
        {
            _recorded.Clear();
        }
    }

    public async Task WaitForRequestAsync(
        string method,
        string path,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_recorded.Any(request =>
                    string.Equals(request.Method, method, StringComparison.Ordinal)
                    && string.Equals(request.Path, path, StringComparison.Ordinal)))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishContainerEventAsync(
        string containerId,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        HttpListenerResponse[] streams;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                streams = [.. _eventStreams];
            }

            if (streams.Length > 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        byte[] line = Encoding.UTF8.GetBytes(
            $"{{\"Type\":\"container\",\"Action\":\"{action}\",\"Actor\":{{\"ID\":\"{containerId}\"}}}}\n");
        for (int index = 0; index < streams.Length; index++)
        {
            try
            {
                await streams[index].OutputStream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await streams[index].OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                RemoveEventStream(streams[index]);
            }
            catch (HttpListenerException)
            {
                RemoveEventStream(streams[index]);
            }
            catch (ObjectDisposedException)
            {
                RemoveEventStream(streams[index]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Close();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }

        Task[] requests;
        lock (_gate)
        {
            requests = [.. _requests];
        }

        try
        {
            await Task.WhenAll(requests).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Task request = HandleAsync(context, cancellationToken);
            lock (_gate)
            {
                _requests.Add(request);
            }
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string wirePath = request.Url?.AbsolutePath ?? "/";
        string path = GetLogicalPath(wirePath);
        byte[] body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _recorded.Add(new DockerEngineRequest(
                request.HttpMethod,
                path,
                request.RawUrl ?? path,
                body,
                request.ContentType));
        }

        bool eventStream = false;
        try
        {
            if (!IsExpectedWirePath(wirePath))
            {
                await WriteErrorAsync(
                    response,
                    400,
                    $"Expected Docker API prefix '{ExpectedApiPrefix()}', not '{wirePath}'.",
                    cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "GET" && path == "/_ping")
            {
                await WriteTextAsync(response, "OK", cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "GET" && path == "/version")
            {
                await WriteJsonAsync(
                    response,
                    new DockerVersionResponse
                    {
                        Version = "fake-engine",
                        ApiVersion = ApiVersion,
                        MinimumApiVersion = MinimumApiVersion,
                        Os = "test",
                        Arch = "test",
                    },
                    DockerEngineJsonContext.Default.DockerVersionResponse,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "GET" && path == "/events")
            {
                eventStream = true;
                await StreamEventsAsync(response, cancellationToken).ConfigureAwait(false);
            }
            else if (path.StartsWith("/images/", StringComparison.Ordinal)
                && path.EndsWith("/json", StringComparison.Ordinal)
                && request.HttpMethod == "GET")
            {
                await InspectImageAsync(path, response, cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "POST" && path == "/images/load")
            {
                await LoadImageAsync(body, response, cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "GET" && path.StartsWith("/networks/", StringComparison.Ordinal))
            {
                await InspectNetworkAsync(path, response, cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "POST" && path == "/networks/create")
            {
                if (NetworkCreateFailure is HttpStatusCode statusCode)
                {
                    await WriteErrorAsync(
                        response,
                        (int)statusCode,
                        "forced network creation failure",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await CreateNetworkAsync(body, response, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (request.HttpMethod == "DELETE" && path.StartsWith("/networks/", StringComparison.Ordinal))
            {
                RemoveNetwork(path, response);
            }
            else if (request.HttpMethod == "GET" && path.StartsWith("/volumes/", StringComparison.Ordinal))
            {
                await InspectVolumeAsync(path, response, cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "POST" && path == "/volumes/create")
            {
                await CreateVolumeAsync(body, response, cancellationToken).ConfigureAwait(false);
            }
            else if (request.HttpMethod == "DELETE" && path.StartsWith("/volumes/", StringComparison.Ordinal))
            {
                RemoveVolume(path, response);
            }
            else if (request.HttpMethod == "POST" && path == "/containers/create")
            {
                await CreateContainerAsync(
                    body,
                    request.QueryString,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (path.StartsWith("/containers/", StringComparison.Ordinal))
            {
                await HandleContainerAsync(
                    request.HttpMethod,
                    path,
                    body,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (path.StartsWith("/exec/", StringComparison.Ordinal))
            {
                await HandleExecAsync(
                    request.HttpMethod,
                    path,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorAsync(response, 404, "route not found", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (!eventStream)
            {
                response.Close();
            }
        }
    }

    private async Task InspectImageAsync(
        string path,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        string reference = DecodeIdentifier(path, "/images/", "/json");
        DockerImageInspectResponse? image;
        lock (_gate)
        {
            _images.TryGetValue(reference, out image);
        }

        if (image is null)
        {
            await WriteErrorAsync(response, 404, "no such image", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            response,
            image,
            DockerEngineJsonContext.Default.DockerImageInspectResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadImageAsync(
        byte[] archive,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        LoadedArchive = archive;
        if (ImageAvailableAfterLoad is string reference)
        {
            AddImage(
                reference,
                ImageIdAfterLoad,
                RepoDigestsAfterLoad ?? [reference]);
        }

        response.ContentType = "application/json";
        await WriteTextAsync(response, "{\"stream\":\"Loaded image\"}\n", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InspectNetworkAsync(
        string path,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        string idOrName = DecodeIdentifier(path, "/networks/");
        NetworkState? network;
        Dictionary<string, DockerNetworkContainer> containers;
        lock (_gate)
        {
            network = FindNetwork(idOrName);
            containers = network is null
                ? []
                : _containers.Values
                    .Where(container => ContainerUsesNetwork(container, network.Request.Name))
                    .ToDictionary(
                        container => container.Id,
                        container => new DockerNetworkContainer { Name = container.Name },
                        StringComparer.Ordinal);
        }

        if (network is null)
        {
            await WriteErrorAsync(response, 404, "no such network", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            response,
            new DockerNetworkInspectResponse
            {
                Id = network.Id,
                Name = network.Request.Name,
                Driver = network.Request.Driver,
                Attachable = network.Request.Attachable is true,
                Labels = network.Request.Labels,
                Containers = containers,
            },
            DockerEngineJsonContext.Default.DockerNetworkInspectResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateNetworkAsync(
        byte[] body,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        DockerNetworkCreateRequest request = Deserialize(
            body,
            DockerEngineJsonContext.Default.DockerNetworkCreateRequest);
        NetworkState network;
        lock (_gate)
        {
            network = new NetworkState($"network-{++_networkSequence}", request);
            _networks[request.Name] = network;
        }

        response.StatusCode = 201;
        await WriteJsonAsync(
            response,
            new DockerNetworkCreateResponse { Id = network.Id },
            DockerEngineJsonContext.Default.DockerNetworkCreateResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private void RemoveNetwork(string path, HttpListenerResponse response)
    {
        string idOrName = DecodeIdentifier(path, "/networks/");
        lock (_gate)
        {
            NetworkState? network = FindNetwork(idOrName);
            if (network is null)
            {
                response.StatusCode = 404;
                return;
            }

            _networks.Remove(network.Request.Name);
            response.StatusCode = 204;
        }
    }

    private async Task InspectVolumeAsync(
        string path,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        string name = DecodeIdentifier(path, "/volumes/");
        VolumeState? volume;
        lock (_gate)
        {
            _volumes.TryGetValue(name, out volume);
        }

        if (volume is null)
        {
            await WriteErrorAsync(response, 404, "no such volume", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            response,
            new DockerVolumeInspectResponse
            {
                Name = volume.Request.Name,
                Driver = volume.Request.Driver,
                Labels = volume.Request.Labels,
            },
            DockerEngineJsonContext.Default.DockerVolumeInspectResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateVolumeAsync(
        byte[] body,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        DockerVolumeCreateRequest request = Deserialize(
            body,
            DockerEngineJsonContext.Default.DockerVolumeCreateRequest);
        lock (_gate)
        {
            _volumes[request.Name] = new VolumeState(request);
        }

        response.StatusCode = 201;
        await WriteJsonAsync(
            response,
            new DockerVolumeInspectResponse
            {
                Name = request.Name,
                Driver = request.Driver,
                Labels = request.Labels,
            },
            DockerEngineJsonContext.Default.DockerVolumeInspectResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private void RemoveVolume(string path, HttpListenerResponse response)
    {
        string name = DecodeIdentifier(path, "/volumes/");
        lock (_gate)
        {
            response.StatusCode = _volumes.Remove(name) ? 204 : 404;
        }
    }

    private async Task CreateContainerAsync(
        byte[] body,
        NameValueCollection query,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        DockerContainerCreateRequest request = Deserialize(
            body,
            DockerEngineJsonContext.Default.DockerContainerCreateRequest);
        string name = query["name"] ?? $"unnamed-{_containerSequence + 1}";
        string id = SeedContainer(name, request, running: false);
        response.StatusCode = 201;
        await WriteJsonAsync(
            response,
            new DockerContainerCreateResponse { Id = id },
            DockerEngineJsonContext.Default.DockerContainerCreateResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleContainerAsync(
        string method,
        string path,
        byte[] body,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        string suffix = path["/containers/".Length..];
        int separator = suffix.IndexOf('/');
        string idOrName = Uri.UnescapeDataString(separator < 0 ? suffix : suffix[..separator]);
        string operation = separator < 0 ? string.Empty : suffix[(separator + 1)..];
        ContainerState? container;
        lock (_gate)
        {
            container = FindContainer(idOrName);
        }

        if (container is null)
        {
            await WriteErrorAsync(response, 404, "no such container", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (method == "GET" && operation == "json")
        {
            await WriteContainerInspectionAsync(container, response, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (method == "POST" && operation == "start")
        {
            lock (_gate)
            {
                container.Running = true;
            }

            response.StatusCode = 204;
        }
        else if (method == "POST" && operation == "stop")
        {
            lock (_gate)
            {
                container.Running = false;
            }

            response.StatusCode = 204;
        }
        else if (method == "PUT" && operation == "archive")
        {
            lock (_gate)
            {
                container.Archive = body;
            }

            response.StatusCode = 200;
        }
        else if (method == "POST" && operation == "exec")
        {
            string execId;
            lock (_gate)
            {
                execId = $"exec-{++_execSequence}";
                _execs[execId] = _execExitCodes.Count == 0 ? 0 : _execExitCodes.Dequeue();
            }

            response.StatusCode = 201;
            await WriteJsonAsync(
                response,
                new DockerExecCreateResponse { Id = execId },
                DockerEngineJsonContext.Default.DockerExecCreateResponse,
                cancellationToken).ConfigureAwait(false);
        }
        else if (method == "DELETE" && operation.Length == 0)
        {
            lock (_gate)
            {
                _containers.Remove(container.Name);
            }

            response.StatusCode = 204;
        }
        else
        {
            await WriteErrorAsync(response, 404, "container route not found", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteContainerInspectionAsync(
        ContainerState container,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        DockerContainerCreateRequest request = container.Request;
        var ports = new Dictionary<string, DockerPortBinding[]?>(StringComparer.Ordinal);
        if (request.HostConfig?.PortBindings is not null)
        {
            foreach ((string key, DockerPortBinding[]? bindings) in request.HostConfig.PortBindings)
            {
                if (bindings is null)
                {
                    ports[key] = null;
                    continue;
                }

                var inspected = new DockerPortBinding[bindings.Length];
                for (int index = 0; index < bindings.Length; index++)
                {
                    string hostPort = string.IsNullOrWhiteSpace(bindings[index].HostPort)
                        ? Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : bindings[index].HostPort!;
                    inspected[index] = new DockerPortBinding
                    {
                        HostIp = bindings[index].HostIp,
                        HostPort = hostPort,
                    };
                }

                ports[key] = inspected;
            }
        }

        bool running;
        int exitCode;
        lock (_gate)
        {
            running = container.Running;
            exitCode = container.ExitCode;
        }

        await WriteJsonAsync(
            response,
            new DockerContainerInspectResponse
            {
                Id = container.Id,
                Name = "/" + container.Name,
                Image = request.Image,
                State = new DockerContainerState
                {
                    Status = running ? "running" : "exited",
                    Running = running,
                    ExitCode = exitCode,
                },
                Config = new DockerContainerConfig
                {
                    Env = request.Env,
                    Image = request.Image,
                    Labels = request.Labels,
                    StopTimeout = request.StopTimeout,
                },
                HostConfig = request.HostConfig,
                NetworkSettings = new DockerNetworkSettings { Ports = ports },
            },
            DockerEngineJsonContext.Default.DockerContainerInspectResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleExecAsync(
        string method,
        string path,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        string suffix = path["/exec/".Length..];
        int separator = suffix.IndexOf('/');
        string id = Uri.UnescapeDataString(separator < 0 ? suffix : suffix[..separator]);
        string operation = separator < 0 ? string.Empty : suffix[(separator + 1)..];
        int exitCode;
        lock (_gate)
        {
            if (!_execs.TryGetValue(id, out exitCode))
            {
                response.StatusCode = 404;
                return;
            }
        }

        if (method == "POST" && operation == "start")
        {
            response.StatusCode = 200;
        }
        else if (method == "GET" && operation == "json")
        {
            await WriteJsonAsync(
                response,
                new DockerExecInspectResponse
                {
                    Id = id,
                    Running = false,
                    ExitCode = exitCode,
                },
                DockerEngineJsonContext.Default.DockerExecInspectResponse,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteErrorAsync(response, 404, "exec route not found", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task StreamEventsAsync(
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.SendChunked = true;
        byte[] initial = "\n"u8.ToArray();
        await response.OutputStream.WriteAsync(initial, cancellationToken).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _eventStreams.Add(response);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveEventStream(response);
            try
            {
                response.Close();
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
            }
        }
    }

    private void RemoveEventStream(HttpListenerResponse response)
    {
        lock (_gate)
        {
            _eventStreams.Remove(response);
        }
    }

    private NetworkState? FindNetwork(string idOrName) =>
        _networks.TryGetValue(idOrName, out NetworkState? named)
            ? named
            : _networks.Values.FirstOrDefault(network => network.Id == idOrName);

    private ContainerState? FindContainer(string idOrName) =>
        _containers.TryGetValue(idOrName, out ContainerState? named)
            ? named
            : _containers.Values.FirstOrDefault(container => container.Id == idOrName);

    private static bool ContainerUsesNetwork(ContainerState container, string network) =>
        string.Equals(container.Request.HostConfig?.NetworkMode, network, StringComparison.Ordinal)
        || container.Request.NetworkingConfig?.EndpointsConfig?.ContainsKey(network) is true;

    private static async Task<byte[]> ReadBodyAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return [];
        }

        using var destination = new MemoryStream();
        await request.InputStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    private static T Deserialize<T>(byte[] body, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(body, typeInfo)
            ?? throw new InvalidDataException("The fake Docker Engine received JSON null.");

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        string value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        await WriteTextAsync(response, message, cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeIdentifier(
        string path,
        string prefix,
        string suffix = "")
    {
        int length = path.Length - prefix.Length - suffix.Length;
        return Uri.UnescapeDataString(path.Substring(prefix.Length, length));
    }

    private static string GetLogicalPath(string path)
    {
        if (!path.StartsWith("/v", StringComparison.Ordinal))
        {
            return path;
        }

        int separator = path.IndexOf('/', 2);
        if (separator < 0
            || !Version.TryParse(path.AsSpan(2, separator - 2), out _))
        {
            return path;
        }

        return path[separator..];
    }

    private bool IsExpectedWirePath(string wirePath)
    {
        if (wirePath is "/_ping" or "/version")
        {
            return true;
        }

        string prefix = ExpectedApiPrefix();
        return wirePath.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private string ExpectedApiPrefix()
    {
        Version server = Version.Parse(ApiVersion);
        Version maximum = new(1, 51);
        Version negotiated = server.CompareTo(maximum) < 0 ? server : maximum;
        return "/v" + negotiated.ToString(2);
    }

    private sealed record NetworkState(string Id, DockerNetworkCreateRequest Request);

    private sealed record VolumeState(DockerVolumeCreateRequest Request);

    private sealed class ContainerState
    {
        public ContainerState(
            string id,
            string name,
            DockerContainerCreateRequest request)
        {
            Id = id;
            Name = name;
            Request = request;
        }

        public string Id { get; }
        public string Name { get; }
        public DockerContainerCreateRequest Request { get; }
        public bool Running { get; set; }
        public int ExitCode { get; set; }
        public byte[]? Archive { get; set; }
    }
}

internal sealed record DockerEngineRequest(
    string Method,
    string Path,
    string RawUrl,
    byte[] Body,
    string? ContentType);
