using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// The Docker Engine API over the BCL's HTTP client, through a Unix socket, a named pipe, or TCP, so
/// the platform takes no Docker SDK. It negotiates the API version once, as the docker CLI does: the
/// highest version both the engine and the client speak.
/// </summary>
internal sealed class DockerEngineClient : IDockerEngine
{
    private const int maximumErrorBodyLength = 16 * 1024;

    // 1.41 is the version Podman's Docker-compatible API reports; 1.51 the newest this client knows.
    private static readonly Version _minimumApiVersion = new(1, 41);
    private static readonly Version _maximumApiVersion = new(1, 51);
    private static readonly MediaTypeWithQualityHeaderValue _jsonAccept = new("application/json");
    private static readonly MediaTypeHeaderValue _jsonMediaType = new("application/json");
    private static readonly TimeSpan _pipeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _negotiation = new(1, 1);
    private string? _prefix;
    private int _isDisposed;

    public DockerEngineClient(DockerEngineEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;
        var handler = new SocketsHttpHandler { UseProxy = false };
        switch (endpoint.Transport)
        {
            case DockerEngineTransport.UnixSocket:
                handler.ConnectCallback = (_, cancellationToken) => ConnectUnixSocketAsync(endpoint.Address, cancellationToken);
                break;
            case DockerEngineTransport.NamedPipe:
                handler.ConnectCallback = (_, cancellationToken) => ConnectNamedPipeAsync(endpoint.Server ?? ".", endpoint.Address, cancellationToken);
                break;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(endpoint.Transport == DockerEngineTransport.Http ? endpoint.Address : "http://docker-engine/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Gets the endpoint the client reaches.</summary>
    public DockerEngineEndpoint Endpoint { get; }

    public Task<DockerImageInspectResponse?> InspectImageAsync(string image, CancellationToken cancellationToken)
    {
        return InspectAsync(GetImageInspectPath(image), DockerEngineJsonContext.Default.DockerImageInspectResponse, cancellationToken);
    }

    public async Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        // The engine reports a failed pull in its stream.
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, GetPullPath(image), null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DockerProgressMessage? progress;
            try
            {
                progress = JsonSerializer.Deserialize(line, DockerEngineJsonContext.Default.DockerProgressMessage);
            }
            catch (JsonException)
            {
                // A Docker-compatible engine may write a plain-text line.
                continue;
            }

            if ((progress?.ErrorDetail?.Message ?? progress?.Error) is { Length: > 0 } error)
            {
                throw new DockerEngineException(HttpMethod.Post, response.RequestMessage?.RequestUri, response.StatusCode, error);
            }
        }
    }

    public Task<DockerNetworkInspectResponse?> InspectNetworkAsync(string name, CancellationToken cancellationToken)
    {
        return InspectAsync(string.Concat("/networks/", Uri.EscapeDataString(name)), DockerEngineJsonContext.Default.DockerNetworkInspectResponse, cancellationToken);
    }

    public async Task CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/networks/create", Json(request, DockerEngineJsonContext.Default.DockerNetworkCreateRequest), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Another gateway, or another step of this one, created it first.
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a network; one that is gone already is not an error. The platform never removes its
    /// network, as a Kubernetes gateway leaves its namespace: tests and tools that end an environment do.
    /// </summary>
    public async Task RemoveNetworkAsync(string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, string.Concat("/networks/", Uri.EscapeDataString(name)), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        var filters = new Dictionary<string, string[]>
        {
            ["label"] = [.. labels.Select(static label => string.Concat(label.Key, "=", label.Value))],
        };
        string query = Uri.EscapeDataString(JsonSerializer.Serialize(filters, DockerEngineJsonContext.Default.DictionaryStringStringArray));
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, string.Concat("/containers/json?all=true&filters=", query), null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, DockerEngineJsonContext.Default.DockerContainerSummaryArray, cancellationToken).ConfigureAwait(false);
    }

    public Task<DockerContainerInspectResponse?> InspectContainerAsync(string container, CancellationToken cancellationToken)
    {
        return InspectAsync(string.Concat("/containers/", Uri.EscapeDataString(container), "/json"), DockerEngineJsonContext.Default.DockerContainerInspectResponse, cancellationToken);
    }

    public async Task<string> CreateContainerAsync(string name, DockerContainerCreateRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            string.Concat("/containers/create?name=", Uri.EscapeDataString(name)),
            Json(request, DockerEngineJsonContext.Default.DockerContainerCreateRequest),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        DockerContainerCreateResponse created = await ReadAsync(response, DockerEngineJsonContext.Default.DockerContainerCreateResponse, cancellationToken).ConfigureAwait(false);
        return created.Id ?? name;
    }

    public async Task StartContainerAsync(string container, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, string.Concat("/containers/", Uri.EscapeDataString(container), "/start"), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadLogsAsync(string container, int lines, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            string.Concat("/containers/", Uri.EscapeDataString(container), "/logs?stdout=true&stderr=true&tail=", lines.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return DemultiplexLogs(body);
    }

    /// <summary>
    /// The path that inspects an image: its reference in the path as the docker CLI sends it, with the
    /// registry's colon, the repository's slashes, and the tag's colon or the digest's at sign literal,
    /// and anything else a path cannot hold escaped. Both Docker's and Podman's routers match the name
    /// against the decoded path, so the literal form is the one neither can read differently.
    /// </summary>
    internal static string GetImageInspectPath(string image)
    {
        var path = new StringBuilder("/images/");
        foreach (char character in image)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '@' or '.' or '-' or '_')
            {
                path.Append(character);
            }
            else
            {
                path.Append(Uri.EscapeDataString(character.ToString()));
            }
        }

        return path.Append("/json").ToString();
    }

    /// <summary>
    /// The path that pulls an image: one tag of it, <c>latest</c> for a reference that names none, as
    /// the docker CLI pulls; the Engine API pulls every tag of a repository named without one.
    /// </summary>
    internal static string GetPullPath(string image)
    {
        (string repository, string tag) = DockerWorkload.SplitPullReference(image);
        return tag.Length == 0
            ? string.Concat("/images/create?fromImage=", Uri.EscapeDataString(repository))
            : string.Concat("/images/create?fromImage=", Uri.EscapeDataString(repository), "&tag=", Uri.EscapeDataString(tag));
    }

    /// <summary>
    /// Reads a container's logs as the engine sends them for a container without a terminal: frames of
    /// an 8-byte header (the stream, three zero bytes, and the payload's big-endian length) and the
    /// payload. A body that is not framed so, as for a container with a terminal, is the text itself.
    /// </summary>
    internal static string DemultiplexLogs(ReadOnlySpan<byte> body)
    {
        var text = new StringBuilder();
        int offset = 0;
        while (offset + 8 <= body.Length)
        {
            ReadOnlySpan<byte> header = body.Slice(offset, 8);
            if (header[0] > 2 || header[1] != 0 || header[2] != 0 || header[3] != 0)
            {
                return offset == 0 ? Encoding.UTF8.GetString(body) : text.ToString();
            }

            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[4..]);
            if (length < 0 || offset + 8 + length > body.Length)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(body.Slice(offset + 8, length)));
            offset += 8 + length;
        }

        return offset == 0 && body.Length > 0 ? Encoding.UTF8.GetString(body) : text.ToString();
    }

    public async Task RemoveContainerAsync(string container, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, string.Concat("/containers/", Uri.EscapeDataString(container), "?force=true"), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _http.Dispose();
    }

    /// <summary>
    /// Chooses the API version to speak: the highest one both the engine and the client support.
    /// </summary>
    /// <exception cref="NotSupportedException">The ranges do not overlap, or the engine reports no version.</exception>
    internal static Version Negotiate(DockerVersionResponse version)
    {
        if (!Version.TryParse(version.ApiVersion, out Version? engineMaximum))
        {
            throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EngineVersionInvalid, version.ApiVersion));
        }

        Version engineMinimum = Version.TryParse(version.MinimumApiVersion, out Version? minimum) ? minimum : new Version(1, 0);
        Version negotiated = engineMaximum < _maximumApiVersion ? engineMaximum : _maximumApiVersion;
        if (negotiated < _minimumApiVersion || negotiated < engineMinimum)
        {
            throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EngineVersionUnsupported, engineMinimum.ToString(2), engineMaximum.ToString(2), _minimumApiVersion.ToString(2), _maximumApiVersion.ToString(2)));
        }

        return new Version(negotiated.Major, negotiated.Minor);
    }

    private async Task<T?> InspectAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken cancellationToken) where T : class
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        string prefix = await GetPrefixAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, prefix + path) { Content = content };
        request.Headers.Accept.Add(_jsonAccept);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new DockerEngineException(method, request.RequestUri, null, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EngineUnreachable, Endpoint), exception);
        }
    }

    private async ValueTask<string> GetPrefixAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _prefix) is { } prefix)
        {
            return prefix;
        }

        await _negotiation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prefix is { } negotiated)
            {
                return negotiated;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/version");
            request.Headers.Accept.Add(_jsonAccept);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new DockerEngineException(HttpMethod.Get, request.RequestUri, null, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EngineUnreachable, Endpoint), exception);
            }

            using (response)
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                DockerVersionResponse version = await ReadAsync(response, DockerEngineJsonContext.Default.DockerVersionResponse, cancellationToken).ConfigureAwait(false);
                prefix = string.Concat("/v", Negotiate(version).ToString(2));
            }

            Volatile.Write(ref _prefix, prefix);
            return prefix;
        }
        finally
        {
            _negotiation.Release();
        }
    }

    private static ByteArrayContent Json<T>(T value, JsonTypeInfo<T> type)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type));
        content.Headers.ContentType = _jsonMediaType;
        return content;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                ?? throw new DockerEngineException(response.RequestMessage?.Method ?? HttpMethod.Get, response.RequestMessage?.RequestUri, response.StatusCode, ErrorMessages.EngineAnswerInvalid);
        }
        catch (JsonException exception)
        {
            throw new DockerEngineException(response.RequestMessage?.Method ?? HttpMethod.Get, response.RequestMessage?.RequestUri, response.StatusCode, ErrorMessages.EngineAnswerInvalid, exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > maximumErrorBodyLength)
        {
            body = string.Concat(body.AsSpan(0, maximumErrorBodyLength), "…");
        }

        throw new DockerEngineException(response.RequestMessage?.Method ?? HttpMethod.Get, response.RequestMessage?.RequestUri, response.StatusCode, body);
    }

    private static async ValueTask<Stream> ConnectUnixSocketAsync(string path, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectNamedPipeAsync(string server, string pipe, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(server, pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // A pipe that does not exist is waited for; an engine that is not running fails within the timeout.
            await stream.ConnectAsync(_pipeTimeout, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
