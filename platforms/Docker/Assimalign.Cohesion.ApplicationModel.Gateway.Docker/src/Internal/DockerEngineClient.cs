using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerEngineClient : IDockerEngineClient
{
    private const int maximumErrorBodyLength = 16 * 1024;
    private const string minimumApiVersion = "1.25";
    private const string maximumApiVersion = "1.51";
    private static readonly MediaTypeWithQualityHeaderValue _jsonAcceptMediaType = new("application/json");
    private static readonly MediaTypeHeaderValue _jsonMediaType = new("application/json");
    private static readonly MediaTypeHeaderValue _tarMediaType = new("application/x-tar");

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private DockerVersionResponse? _version;
    private string? _apiPrefix;
    private volatile bool _disposed;

    public DockerEngineClient(string endpoint)
        : this(CreateEndpointUri(endpoint))
    {
    }

    public DockerEngineClient(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The Docker Engine endpoint must be an absolute URI.", nameof(endpoint));
        }

        SocketsHttpHandler handler = CreateHandler(endpoint);
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = CreateBaseAddress(endpoint),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/_ping");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        _ = await GetVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DockerVersionResponse> GetVersionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DockerVersionResponse? current = Volatile.Read(ref _version);
        if (current is not null)
        {
            return current;
        }

        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _version;
            if (current is not null)
            {
                return current;
            }

            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/version");
            using HttpResponseMessage response = await SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            current = await DeserializeResponseAsync(
                response,
                DockerEngineJsonContext.Default.DockerVersionResponse,
                cancellationToken).ConfigureAwait(false);
            string prefix = SelectApiPrefix(current);
            Volatile.Write(ref _apiPrefix, prefix);
            Volatile.Write(ref _version, current);
            return current;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    public Task<DockerImageInspectResponse?> InspectImageAsync(
        string image,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(
            $"/images/{EncodePathSegment(image, nameof(image))}/json",
            DockerEngineJsonContext.Default.DockerImageInspectResponse,
            cancellationToken);
    }

    public async Task LoadImageAsync(
        Stream imageArchive,
        bool quiet = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageArchive);
        ThrowIfDisposed();

        string path = await GetApiPathAsync(
            $"/images/load?quiet={FormatBoolean(quiet)}",
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        request.Content = new BorrowedStreamContent(imageArchive, _tarMediaType);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await ReadProgressResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<DockerNetworkInspectResponse?> InspectNetworkAsync(
        string networkIdOrName,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(
            $"/networks/{EncodePathSegment(networkIdOrName, nameof(networkIdOrName))}",
            DockerEngineJsonContext.Default.DockerNetworkInspectResponse,
            cancellationToken);
    }

    public Task<DockerNetworkCreateResponse> CreateNetworkAsync(
        DockerNetworkCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync(
            HttpMethod.Post,
            "/networks/create",
            request,
            DockerEngineJsonContext.Default.DockerNetworkCreateRequest,
            DockerEngineJsonContext.Default.DockerNetworkCreateResponse,
            cancellationToken);
    }

    public Task RemoveNetworkAsync(
        string networkIdOrName,
        CancellationToken cancellationToken = default)
    {
        return SendIdempotentAsync(
            HttpMethod.Delete,
            $"/networks/{EncodePathSegment(networkIdOrName, nameof(networkIdOrName))}",
            cancellationToken);
    }

    public Task<DockerVolumeInspectResponse?> InspectVolumeAsync(
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(
            $"/volumes/{EncodePathSegment(volumeName, nameof(volumeName))}",
            DockerEngineJsonContext.Default.DockerVolumeInspectResponse,
            cancellationToken);
    }

    public Task<DockerVolumeInspectResponse> CreateVolumeAsync(
        DockerVolumeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync(
            HttpMethod.Post,
            "/volumes/create",
            request,
            DockerEngineJsonContext.Default.DockerVolumeCreateRequest,
            DockerEngineJsonContext.Default.DockerVolumeInspectResponse,
            cancellationToken);
    }

    public Task RemoveVolumeAsync(
        string volumeName,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        string path = $"/volumes/{EncodePathSegment(volumeName, nameof(volumeName))}?force={FormatBoolean(force)}";
        return SendIdempotentAsync(HttpMethod.Delete, path, cancellationToken);
    }

    public Task<DockerContainerInspectResponse?> InspectContainerAsync(
        string containerIdOrName,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(
            $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}/json",
            DockerEngineJsonContext.Default.DockerContainerInspectResponse,
            cancellationToken);
    }

    public Task<DockerContainerCreateResponse> CreateContainerAsync(
        string? name,
        DockerContainerCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string path = string.IsNullOrEmpty(name)
            ? "/containers/create"
            : $"/containers/create?name={EncodeQueryValue(name)}";

        return SendJsonAsync(
            HttpMethod.Post,
            path,
            request,
            DockerEngineJsonContext.Default.DockerContainerCreateRequest,
            DockerEngineJsonContext.Default.DockerContainerCreateResponse,
            cancellationToken);
    }

    public async Task StartContainerAsync(
        string containerIdOrName,
        CancellationToken cancellationToken = default)
    {
        string path = await GetApiPathAsync(
            $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}/start",
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopContainerAsync(
        string containerIdOrName,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "The stop timeout cannot be negative.");
        }

        string path = $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}/stop";
        if (timeoutSeconds is not null)
        {
            path = $"{path}?t={timeoutSeconds.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        path = await GetApiPathAsync(path, cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotModified)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveContainerAsync(
        string containerIdOrName,
        bool force = false,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default)
    {
        string path = $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}"
            + $"?force={FormatBoolean(force)}&v={FormatBoolean(removeVolumes)}";
        return SendIdempotentAsync(HttpMethod.Delete, path, cancellationToken);
    }

    public async Task PutArchiveAsync(
        string containerIdOrName,
        string containerPath,
        Stream archive,
        bool noOverwriteDirectoryWithNonDirectory = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        ArgumentNullException.ThrowIfNull(archive);
        ThrowIfDisposed();

        string path = $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}/archive"
            + $"?path={EncodeQueryValue(containerPath)}"
            + $"&noOverwriteDirNonDir={FormatBoolean(noOverwriteDirectoryWithNonDirectory)}";
        path = await GetApiPathAsync(path, cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, path);
        request.Content = new BorrowedStreamContent(archive, _tarMediaType);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<DockerExecCreateResponse> CreateExecAsync(
        string containerIdOrName,
        DockerExecCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = $"/containers/{EncodePathSegment(containerIdOrName, nameof(containerIdOrName))}/exec";
        return SendJsonAsync(
            HttpMethod.Post,
            path,
            request,
            DockerEngineJsonContext.Default.DockerExecCreateRequest,
            DockerEngineJsonContext.Default.DockerExecCreateResponse,
            cancellationToken);
    }

    public async Task StartExecAsync(
        string execId,
        DockerExecStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = await GetApiPathAsync(
            $"/exec/{EncodePathSegment(execId, nameof(execId))}/start",
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage httpRequest = CreateJsonRequest(
            HttpMethod.Post,
            path,
            request,
            DockerEngineJsonContext.Default.DockerExecStartRequest);

        using HttpResponseMessage response = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await responseStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
    }

    public Task<DockerExecInspectResponse?> InspectExecAsync(
        string execId,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(
            $"/exec/{EncodePathSegment(execId, nameof(execId))}/json",
            DockerEngineJsonContext.Default.DockerExecInspectResponse,
            cancellationToken);
    }

    public async IAsyncEnumerable<DockerEventMessage> GetEventsAsync(
        DockerEventsQuery? query = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string path = await GetApiPathAsync(BuildEventsPath(query), cancellationToken)
            .ConfigureAwait(false);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DockerEventMessage? message = JsonSerializer.Deserialize(
                line,
                DockerEngineJsonContext.Default.DockerEventMessage);

            if (message is null)
            {
                throw CreateInvalidJsonException(request, response.StatusCode, "The Docker event stream contained a null JSON value.");
            }

            yield return message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        // SemaphoreSlim.Dispose cannot race safely with an in-flight first-use negotiation.
        // No wait handle is requested, so leaving this private gate undisposed owns no OS handle.
    }

    private static Uri CreateEndpointUri(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            throw new ArgumentException("The Docker Engine endpoint must be an absolute URI.", nameof(endpoint));
        }

        return endpointUri;
    }

    private static SocketsHttpHandler CreateHandler(Uri endpoint)
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
        };

        switch (endpoint.Scheme.ToLowerInvariant())
        {
            case "http":
            case "https":
                break;

            case "unix":
                string socketPath = Uri.UnescapeDataString(endpoint.AbsolutePath);
                if (string.IsNullOrEmpty(socketPath) || socketPath == "/")
                {
                    handler.Dispose();
                    throw new ArgumentException("The unix Docker endpoint must include a socket path.", nameof(endpoint));
                }

                handler.ConnectCallback = (_, cancellationToken) => ConnectUnixSocketAsync(socketPath, cancellationToken);
                break;

            case "npipe":
                (string serverName, string pipeName) = ParseNamedPipeEndpoint(endpoint);
                handler.ConnectCallback = (_, cancellationToken) => ConnectNamedPipeAsync(
                    serverName,
                    pipeName,
                    cancellationToken);
                break;

            default:
                handler.Dispose();
                throw new NotSupportedException(
                    $"Docker Engine endpoint scheme '{endpoint.Scheme}' is not supported. Use http, https, unix, or npipe.");
        }

        return handler;
    }

    private static Uri CreateBaseAddress(Uri endpoint)
    {
        if (endpoint.Scheme is "http" or "https")
        {
            UriBuilder builder = new(endpoint)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri;
        }

        return new Uri("http://docker-engine/", UriKind.Absolute);
    }

    private static async ValueTask<Stream> ConnectUnixSocketAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool transferOwnership = false;

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            NetworkStream stream = new(socket, ownsSocket: true);
            transferOwnership = true;
            return stream;
        }
        finally
        {
            if (!transferOwnership)
            {
                socket.Dispose();
            }
        }
    }

    private static async ValueTask<Stream> ConnectNamedPipeAsync(
        string serverName,
        string pipeName,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = new(
            serverName,
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        bool transferOwnership = false;

        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            transferOwnership = true;
            return pipe;
        }
        finally
        {
            if (!transferOwnership)
            {
                pipe.Dispose();
            }
        }
    }

    private static (string ServerName, string PipeName) ParseNamedPipeEndpoint(Uri endpoint)
    {
        string serverName = string.IsNullOrWhiteSpace(endpoint.Host) ? "." : endpoint.Host;
        string path = Uri.UnescapeDataString(endpoint.AbsolutePath).Replace('\\', '/');
        const string pipePrefix = "/pipe/";

        if (!path.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase)
            || path.Length == pipePrefix.Length)
        {
            throw new ArgumentException(
                "The npipe Docker endpoint must have the form npipe://./pipe/docker_engine.",
                nameof(endpoint));
        }

        string pipeName = path[pipePrefix.Length..];
        if (pipeName.Contains("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The npipe Docker endpoint contains an invalid pipe name.", nameof(endpoint));
        }

        return (serverName, pipeName);
    }

    private static string BuildEventsPath(DockerEventsQuery? query)
    {
        if (query is null)
        {
            return "/events";
        }

        List<string> parameters = [];

        if (query.Since is not null)
        {
            parameters.Add($"since={query.Since.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (query.Until is not null)
        {
            parameters.Add($"until={query.Until.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (query.Filters is { Count: > 0 })
        {
            string filters = JsonSerializer.Serialize(
                query.Filters,
                DockerEngineJsonContext.Default.DictionaryStringStringArray);
            parameters.Add($"filters={EncodeQueryValue(filters)}");
        }

        return parameters.Count == 0
            ? "/events"
            : $"/events?{string.Join('&', parameters)}";
    }

    private async Task<T?> InspectAsync<T>(
        string path,
        JsonTypeInfo<T> responseTypeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        path = await GetApiPathAsync(path, cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest value,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        path = await GetApiPathAsync(path, cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateJsonRequest(method, path, value, requestTypeInfo);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendIdempotentAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        path = await GetApiPathAsync(path, cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateRequest(method, path);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Accept.Add(_jsonAcceptMediaType);
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest<T>(
        HttpMethod method,
        string path,
        T value,
        JsonTypeInfo<T> requestTypeInfo)
        where T : class
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, requestTypeInfo);
        HttpRequestMessage request = CreateRequest(method, path);
        request.Content = new ByteArrayContent(json);
        request.Content.Headers.ContentType = _jsonMediaType;
        return request;
    }

    private static async Task<T> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> responseTypeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            T? result = await JsonSerializer.DeserializeAsync(
                stream,
                responseTypeInfo,
                cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                throw CreateInvalidJsonException(
                    response.RequestMessage,
                    response.StatusCode,
                    "The Docker Engine returned a null JSON value.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new DockerEngineException(
                response.RequestMessage?.Method ?? HttpMethod.Get,
                response.RequestMessage?.RequestUri,
                response.StatusCode,
                "The Docker Engine returned invalid JSON.",
                exception);
        }
    }

    private static async Task ReadProgressResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DockerProgressMessage? progress;
            try
            {
                progress = JsonSerializer.Deserialize(
                    line,
                    DockerEngineJsonContext.Default.DockerProgressMessage);
            }
            catch (JsonException)
            {
                // Docker-compatible engines may return a plain-text success message.
                continue;
            }

            string? error = progress?.ErrorDetail?.Message ?? progress?.Error;
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new DockerEngineException(
                    response.RequestMessage?.Method ?? HttpMethod.Post,
                    response.RequestMessage?.RequestUri,
                    response.StatusCode,
                    error);
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw new DockerEngineException(
            response.RequestMessage?.Method ?? HttpMethod.Get,
            response.RequestMessage?.RequestUri,
            response.StatusCode,
            body);
    }

    private static async Task<string?> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char[] buffer = new char[maximumErrorBodyLength + 1];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

        if (count == 0)
        {
            return null;
        }

        int bodyLength = Math.Min(count, maximumErrorBodyLength);
        string body = new(buffer, 0, bodyLength);
        if (count > maximumErrorBodyLength)
        {
            body += "…";
        }

        return body;
    }

    private static DockerEngineException CreateInvalidJsonException(
        HttpRequestMessage? request,
        HttpStatusCode statusCode,
        string message)
    {
        return new DockerEngineException(
            request?.Method ?? HttpMethod.Get,
            request?.RequestUri,
            statusCode,
            message);
    }

    private static string EncodePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        return Uri.EscapeDataString(value);
    }

    private static string EncodeQueryValue(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private async ValueTask<string> GetApiPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _apiPrefix) is not string prefix)
        {
            _ = await GetVersionAsync(cancellationToken).ConfigureAwait(false);
            prefix = Volatile.Read(ref _apiPrefix)
                ?? throw new InvalidOperationException("Docker API negotiation did not select a version.");
        }

        return prefix + path;
    }

    private static string SelectApiPrefix(DockerVersionResponse response)
    {
        if (!Version.TryParse(response.ApiVersion, out Version? serverMaximum))
        {
            throw new NotSupportedException(
                $"Docker Engine reported invalid API version '{response.ApiVersion}'.");
        }

        Version clientMinimum = Version.Parse(minimumApiVersion);
        Version clientMaximum = Version.Parse(maximumApiVersion);
        Version serverMinimum = string.IsNullOrWhiteSpace(response.MinimumApiVersion)
            ? new Version(1, 0)
            : Version.TryParse(response.MinimumApiVersion, out Version? parsedMinimum)
                ? parsedMinimum
                : throw new NotSupportedException(
                    $"Docker Engine reported invalid minimum API version '{response.MinimumApiVersion}'.");
        Version negotiated = serverMaximum.CompareTo(clientMaximum) < 0
            ? serverMaximum
            : clientMaximum;
        if (negotiated.CompareTo(clientMinimum) < 0
            || negotiated.CompareTo(serverMinimum) < 0)
        {
            throw new NotSupportedException(
                $"Docker Engine API range '{serverMinimum.ToString(2)}' through "
                + $"'{serverMaximum.ToString(2)}' does not overlap supported client range "
                + $"'{minimumApiVersion}' through '{maximumApiVersion}'.");
        }

        return "/v" + negotiated.ToString(2);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class BorrowedStreamContent : HttpContent
    {
        private readonly Stream _source;

        public BorrowedStreamContent(Stream source, MediaTypeHeaderValue contentType)
        {
            _source = source;
            Headers.ContentType = contentType;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _source.CopyToAsync(stream);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            return _source.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_source.CanSeek)
            {
                length = _source.Length - _source.Position;
                return true;
            }

            length = 0;
            return false;
        }
    }
}
