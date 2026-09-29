using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class EmbeddedOciRegistry : IEmbeddedOciRegistry
{
    private const int maximumConcurrentConnections = 64;
    private const int maximumHeaderBytes = 32 * 1024;
    private static readonly TimeSpan _requestHeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] _emptyObject = "{}"u8.ToArray();
    private static readonly byte[] _blobUnknown =
        "{\"errors\":[{\"code\":\"BLOB_UNKNOWN\",\"message\":\"blob unknown to registry\"}]}"u8.ToArray();
    private static readonly byte[] _manifestUnknown =
        "{\"errors\":[{\"code\":\"MANIFEST_UNKNOWN\",\"message\":\"manifest unknown\"}]}"u8.ToArray();
    private static readonly byte[] _nameUnknown =
        "{\"errors\":[{\"code\":\"NAME_UNKNOWN\",\"message\":\"repository name unknown\"}]}"u8.ToArray();
    private static readonly byte[] _methodNotAllowed =
        "{\"errors\":[{\"code\":\"UNSUPPORTED\",\"message\":\"pull-only registry\"}]}"u8.ToArray();

    private readonly object _gate = new();
    private readonly HashSet<Task> _connections = [];
    private readonly SemaphoreSlim _connectionSlots = new(
        maximumConcurrentConnections,
        maximumConcurrentConnections);
    private readonly int _port;
    private readonly OciImageStore _store;
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Uri? _endpoint;

    public EmbeddedOciRegistry(string storePath, int port)
    {
        _store = new OciImageStore(storePath);
        _port = port;
    }

    public Uri Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _endpoint
                    ?? throw new InvalidOperationException("The embedded OCI registry is not running.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_listener is not null)
            {
                return Task.CompletedTask;
            }

            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start(maximumConcurrentConnections);
            var lifetime = new CancellationTokenSource();
            _listener = listener;
            _lifetime = lifetime;
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _endpoint = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
            _acceptLoop = AcceptAsync(listener, lifetime.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? acceptLoop;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (_listener is null)
            {
                return;
            }

            lifetime = _lifetime;
            lifetime?.Cancel();
            _listener.Stop();
            _listener = null;
            _lifetime = null;
            _endpoint = null;
            acceptLoop = _acceptLoop;
            _acceptLoop = null;
        }

        try
        {
            if (acceptLoop is not null)
            {
                await IgnoreExpectedShutdownAsync(acceptLoop, cancellationToken).ConfigureAwait(false);
            }

            Task[] connections;
            lock (_gate)
            {
                connections = [.. _connections];
            }

            if (connections.Length != 0)
            {
                await Task.WhenAll(connections).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifetime?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await AcceptClientAsync(listener, cancellationToken)
                .ConfigureAwait(false);
            Task connection = HandleConnectionAsync(client, cancellationToken);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            _ = connection.ContinueWith(
                completed =>
                {
                    lock (_gate)
                    {
                        _connections.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<TcpClient> AcceptClientAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool releaseSlot = true;
        try
        {
            TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            releaseSlot = false;
            return client;
        }
        finally
        {
            if (releaseSlot)
            {
                _connectionSlots.Release();
            }
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (IOException)
        {
            client.Dispose();
        }
        catch (SocketException)
        {
            client.Dispose();
        }
        catch (InvalidDataException)
        {
            client.Dispose();
        }
        catch (UriFormatException)
        {
        }
        finally
        {
            client.Dispose();
            _connectionSlots.Release();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (NetworkStream stream = client.GetStream())
        {
            using var headerLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            headerLifetime.CancelAfter(_requestHeaderTimeout);
            RegistryRequest? request;
            try
            {
                request = await ReadRequestAsync(stream, headerLifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (request is null)
            {
                return;
            }

            if (request.Method is not "GET" and not "HEAD")
            {
                await WriteBytesAsync(
                    stream,
                    request.Method == "HEAD",
                    405,
                    "Method Not Allowed",
                    "application/json",
                    _methodNotAllowed,
                    null,
                    "Allow: GET, HEAD\r\n",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(request.Path, "/v2/", StringComparison.Ordinal))
            {
                await WriteBytesAsync(
                    stream,
                    request.Method == "HEAD",
                    200,
                    "OK",
                    "application/json",
                    _emptyObject,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!request.Path.StartsWith("/v2/", StringComparison.Ordinal))
            {
                await WriteBytesAsync(
                    stream,
                    request.Method == "HEAD",
                    404,
                    "Not Found",
                    "application/json",
                    _nameUnknown,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (TrySplit(request.Path, "/manifests/", out string repository, out string reference))
            {
                if (!_store.TryGetManifest(repository, reference, out StoredImageContent manifest))
                {
                    await WriteBytesAsync(
                        stream,
                        request.Method == "HEAD",
                        404,
                        "Not Found",
                        "application/json",
                        _manifestUnknown,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteFileAsync(stream, request.Method == "HEAD", manifest, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (TrySplit(
                    request.Path,
                    "/blobs/",
                    out string blobRepository,
                    out string blobDigest)
                && _store.TryGetBlob(blobRepository, blobDigest, out StoredImageContent blob))
            {
                await WriteFileAsync(stream, request.Method == "HEAD", blob, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteBytesAsync(
                stream,
                request.Method == "HEAD",
                404,
                "Not Found",
                "application/json",
                _blobUnknown,
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RegistryRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        byte[] buffer = new byte[1024];
        while (bytes.Count < maximumHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            for (int index = 0; index < read; index++)
            {
                bytes.Add(buffer[index]);
                int count = bytes.Count;
                if (count >= 4
                    && bytes[count - 4] == '\r'
                    && bytes[count - 3] == '\n'
                    && bytes[count - 2] == '\r'
                    && bytes[count - 1] == '\n')
                {
                    string headers = Encoding.ASCII.GetString([.. bytes]);
                    int end = headers.IndexOf("\r\n", StringComparison.Ordinal);
                    if (end <= 0)
                    {
                        return null;
                    }

                    string[] parts = headers[..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    string path = parts[1];
                    int query = path.IndexOf('?');
                    if (query >= 0)
                    {
                        path = path[..query];
                    }

                    return new RegistryRequest(parts[0], Uri.UnescapeDataString(path));
                }
            }
        }

        throw new InvalidDataException(
            $"Registry request headers exceed {maximumHeaderBytes} bytes.");
    }

    private static bool TrySplit(
        string path,
        string marker,
        out string repository,
        out string reference)
    {
        string relative = path["/v2/".Length..];
        int markerIndex = relative.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0 || markerIndex + marker.Length >= relative.Length)
        {
            repository = string.Empty;
            reference = string.Empty;
            return false;
        }

        repository = relative[..markerIndex];
        reference = relative[(markerIndex + marker.Length)..];
        return true;
    }

    private static async Task WriteFileAsync(
        Stream stream,
        bool head,
        StoredImageContent content,
        CancellationToken cancellationToken)
    {
        await WriteHeadersAsync(
            stream,
            200,
            "OK",
            content.MediaType,
            content.Length,
            content.Digest,
            null,
            cancellationToken).ConfigureAwait(false);
        if (head)
        {
            return;
        }

        await using FileStream file = new(
            content.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);
        await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        Stream stream,
        bool head,
        int status,
        string reason,
        string mediaType,
        byte[] body,
        string? digest,
        string? additionalHeaders,
        CancellationToken cancellationToken)
    {
        await WriteHeadersAsync(
            stream,
            status,
            reason,
            mediaType,
            body.LongLength,
            digest,
            additionalHeaders,
            cancellationToken).ConfigureAwait(false);
        if (!head)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteHeadersAsync(
        Stream stream,
        int status,
        string reason,
        string mediaType,
        long length,
        string? digest,
        string? additionalHeaders,
        CancellationToken cancellationToken)
    {
        string headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Docker-Distribution-Api-Version: registry/2.0\r\n" +
            $"Content-Type: {mediaType}\r\n" +
            (digest is null ? string.Empty : $"Docker-Content-Digest: {digest}\r\n") +
            (additionalHeaders ?? string.Empty) +
            $"Content-Length: {length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task IgnoreExpectedShutdownAsync(
        Task acceptLoop,
        CancellationToken cancellationToken)
    {
        try
        {
            await acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private sealed record RegistryRequest(string Method, string Path);
}
