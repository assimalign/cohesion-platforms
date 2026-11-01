using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Platforms;

/// <summary>
/// Serves an instance's control plane over HTTP on a port of its own, for the instance half of a
/// platform whose gateway calls the instance there.
/// </summary>
/// <remarks>
/// <para>
/// Linked source, not a package: each platform's instance half compiles this file into its own
/// assembly, so no platform package references another. A platform adds routes of its own, such as
/// Docker's publication channel, ahead of the control plane's.
/// </para>
/// <para>
/// Control-plane routes: <c>GET /manifest</c>; <c>GET /actions/{name}</c> reads an action and
/// <c>POST /actions/{name}</c> writes it with the body as its input; <c>PUT /state/{name}</c> restores
/// its state with the body as the snapshot's value. An action or a restore answers with its
/// <see cref="ApplicationActionResult"/>, written by the control plane's serializer, and a status:
/// 200 succeeded, 400 rejected, 404 no such action, 405 operation not accepted. Until a control plane
/// is served (<see cref="Serve"/>), they answer 503, which is no answer.
/// </para>
/// <para>
/// The port is the instance's own, apart from the application's listener and middleware, so the
/// application never sees a gateway's call. It listens on every address (on Windows, the loopback
/// name only, which needs no URL reservation). Each request runs in the execution context the
/// listener started in, the instance's. A body is read only by a route that takes one, and one
/// longer than <see cref="MaximumBodyLength"/> is refused with 413.
/// </para>
/// </remarks>
internal sealed class InstanceControlPlaneListener : IAsyncDisposable
{
    /// <summary>The longest request body the listener reads: 4 MiB.</summary>
    public const int MaximumBodyLength = 4 * 1024 * 1024;

    private static readonly TimeSpan _acceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly HttpListener _listener = new();
    private readonly InstanceControlPlaneRoutes? _routes;
    private readonly ConcurrentDictionary<Task, byte> _handling = new();
    private IApplicationInstanceControlPlane? _controlPlane;
    private Task _accepting = Task.CompletedTask;
    private int _isStopped;

    private InstanceControlPlaneListener(IApplicationInstanceControlPlane? controlPlane, InstanceControlPlaneRoutes? routes)
    {
        _controlPlane = controlPlane;
        _routes = routes;
    }

    /// <summary>
    /// Starts listening on the port.
    /// </summary>
    /// <param name="port">The port.</param>
    /// <param name="controlPlane">The control plane to serve at once; <see langword="null"/> to serve none until <see cref="Serve"/>.</param>
    /// <param name="routes">The platform's own routes, tried before the control plane's; <see langword="null"/> for none.</param>
    /// <returns>The listener.</returns>
    /// <exception cref="HttpListenerException">The port cannot be listened on.</exception>
    public static InstanceControlPlaneListener Start(int port, IApplicationInstanceControlPlane? controlPlane = null, InstanceControlPlaneRoutes? routes = null)
    {
        var listener = new InstanceControlPlaneListener(controlPlane, routes);
        listener._listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://localhost:{port}/" : $"http://*:{port}/");
        listener._listener.Start();
        listener._accepting = Task.Run(listener.AcceptAsync);
        return listener;
    }

    /// <summary>
    /// Starts serving the control plane's routes.
    /// </summary>
    /// <param name="controlPlane">The control plane.</param>
    public void Serve(IApplicationInstanceControlPlane controlPlane)
    {
        Volatile.Write(ref _controlPlane, controlPlane);
    }

    /// <summary>
    /// Stops listening, waits for the requests still being handled, and closes the port.
    /// </summary>
    /// <returns>A task that completes when the port is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isStopped, 1) != 0)
        {
            return;
        }

        _listener.Stop();
        await _accepting.ConfigureAwait(false);

        // The requests still being handled finish before the port closes, so none reaches a control
        // plane its instance stopped after this returns. A handler never faults.
        await Task.WhenAll(_handling.Keys).ConfigureAwait(false);
        _listener.Close();
    }

    private async Task AcceptAsync()
    {
        while (Volatile.Read(ref _isStopped) == 0)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (Volatile.Read(ref _isStopped) != 0 || !_listener.IsListening)
            {
                return;
            }
            catch (Exception)
            {
                // Deliberately broad: a request that fails before it is accepted, such as one whose
                // client reset its connection, never stops the port answering the next one.
                await Task.Delay(_acceptRetryDelay).ConfigureAwait(false);
                continue;
            }

            Track(HandleAsync(context));
        }
    }

    // Keeps a request's handler until it completes, so disposal can wait for it.
    private void Track(Task handling)
    {
        _handling.TryAdd(handling, 0);
        _ = handling.ContinueWith(
            static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
            _handling,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            InstanceControlPlaneResponse answer;
            try
            {
                answer = await DispatchAsync(context.Request).ConfigureAwait(false);
            }
            catch (InstanceControlPlaneBodyTooLargeException)
            {
                answer = InstanceControlPlaneResponse.Empty(HttpStatusCode.RequestEntityTooLarge);
            }

            response.StatusCode = answer.Status;
            if (!answer.Body.IsEmpty)
            {
                response.ContentType = answer.ContentType;
                response.ContentLength64 = answer.Body.Length;
                await response.OutputStream.WriteAsync(answer.Body).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Deliberately broad: a fault is no answer, and the gateway treats the instance as gone.
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch (InvalidOperationException)
            {
                // The headers were already sent.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // Deliberately broad: a connection the client already dropped cannot be closed cleanly,
                // and the handler never faults.
            }
        }
    }

    private async Task<InstanceControlPlaneResponse> DispatchAsync(HttpListenerRequest request)
    {
        string[] segments = request.Url?.AbsolutePath.Trim('/').Split('/') ?? [];
        if (_routes is not null && await _routes.Invoke(request.HttpMethod, segments, () => ReadBodyAsync(request)).ConfigureAwait(false) is { } routed)
        {
            return routed;
        }

        if (Volatile.Read(ref _controlPlane) is not { } controlPlane)
        {
            return InstanceControlPlaneResponse.Empty(HttpStatusCode.ServiceUnavailable);
        }

        if (segments is ["manifest"])
        {
            return request.HttpMethod == "GET"
                ? new InstanceControlPlaneResponse((int)HttpStatusCode.OK, controlPlane.Serializer.Serialize(controlPlane.Manifest), controlPlane.Serializer.MediaType)
                : InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
        }

        if (segments is not [("actions" or "state") and string route, string name])
        {
            return InstanceControlPlaneResponse.Empty(HttpStatusCode.NotFound);
        }

        if (!ActionName.TryCreate(Uri.UnescapeDataString(name), out ActionName action))
        {
            return Answer(controlPlane, ApplicationActionResult.NotFound());
        }

        ApplicationActionResult result;
        if (route == "state")
        {
            if (request.HttpMethod != "PUT")
            {
                return InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
            }

            ReadOnlyMemory<byte> snapshot = await ReadBodyAsync(request).ConfigureAwait(false);
            result = await controlPlane.RestoreAsync(ApplicationActionSnapshot.Create(action, snapshot)).ConfigureAwait(false);
        }
        else
        {
            ApplicationActionOperation? method = request.HttpMethod switch
            {
                "GET" => ApplicationActionOperation.Read,
                "POST" => ApplicationActionOperation.Write,
                _ => null,
            };
            if (method is not { } operation)
            {
                return InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
            }

            ApplicationActionContext invocation = operation == ApplicationActionOperation.Write
                ? ApplicationActionContext.Create(operation, controlPlane.Serializer, await ReadBodyAsync(request).ConfigureAwait(false))
                : ApplicationActionContext.Create(operation, controlPlane.Serializer);
            result = await controlPlane.ExecuteAsync(action, invocation).ConfigureAwait(false);
        }

        return Answer(controlPlane, result);
    }

    private static InstanceControlPlaneResponse Answer(IApplicationInstanceControlPlane controlPlane, ApplicationActionResult result)
    {
        HttpStatusCode status = result.Status switch
        {
            ApplicationActionStatus.Succeeded => HttpStatusCode.OK,
            ApplicationActionStatus.Rejected => HttpStatusCode.BadRequest,
            ApplicationActionStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.MethodNotAllowed,
        };

        return new InstanceControlPlaneResponse((int)status, controlPlane.Serializer.Serialize(result), controlPlane.Serializer.MediaType);
    }

    // Reads the body, up to MaximumBodyLength; a longer one, declared or sent, is refused.
    private static async ValueTask<ReadOnlyMemory<byte>> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (request.ContentLength64 > MaximumBodyLength)
        {
            throw new InstanceControlPlaneBodyTooLargeException();
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBodyLength)
            {
                throw new InstanceControlPlaneBodyTooLargeException();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// A request body longer than <see cref="InstanceControlPlaneListener.MaximumBodyLength"/>, which the
/// listener answers with 413.
/// </summary>
internal sealed class InstanceControlPlaneBodyTooLargeException : Exception
{
    public InstanceControlPlaneBodyTooLargeException()
        : base("The request body is longer than an instance's control-plane port reads.")
    {
    }
}

/// <summary>
/// A platform's own routes on an instance's control-plane port, tried before the control plane's.
/// </summary>
/// <param name="method">The request's HTTP method.</param>
/// <param name="segments">The request path's segments, still escaped.</param>
/// <param name="readBody">
/// Reads the request's body, empty for none, for a route that takes one. It throws
/// <see cref="InstanceControlPlaneBodyTooLargeException"/>, which the listener answers with 413, for a
/// body longer than <see cref="InstanceControlPlaneListener.MaximumBodyLength"/>.
/// </param>
/// <returns>The answer, or <see langword="null"/> when the route is not the platform's.</returns>
internal delegate ValueTask<InstanceControlPlaneResponse?> InstanceControlPlaneRoutes(string method, string[] segments, Func<ValueTask<ReadOnlyMemory<byte>>> readBody);

/// <summary>
/// What an instance's control-plane port answers a request with.
/// </summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body; empty for none.</param>
/// <param name="ContentType">The body's media type; <see langword="null"/> for none.</param>
internal readonly record struct InstanceControlPlaneResponse(int Status, ReadOnlyMemory<byte> Body, string? ContentType)
{
    /// <summary>An answer with a status and no body.</summary>
    public static InstanceControlPlaneResponse Empty(HttpStatusCode status)
    {
        return new InstanceControlPlaneResponse((int)status, ReadOnlyMemory<byte>.Empty, null);
    }
}
