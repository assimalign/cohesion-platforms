using System;
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
/// listener started in, the instance's.
/// </para>
/// </remarks>
internal sealed class InstanceControlPlaneListener : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly InstanceControlPlaneRoutes? _routes;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isStopped, 1) != 0)
        {
            return;
        }

        _listener.Stop();
        await _accepting.ConfigureAwait(false);
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

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            InstanceControlPlaneResponse answer = await DispatchAsync(context.Request).ConfigureAwait(false);
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
            response.Close();
        }
    }

    private async Task<InstanceControlPlaneResponse> DispatchAsync(HttpListenerRequest request)
    {
        string[] segments = request.Url?.AbsolutePath.Trim('/').Split('/') ?? [];
        byte[] body = await ReadBodyAsync(request).ConfigureAwait(false);
        if (_routes is not null && await _routes.Invoke(request.HttpMethod, segments, body).ConfigureAwait(false) is { } routed)
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

            result = await controlPlane.RestoreAsync(ApplicationActionSnapshot.Create(action, body)).ConfigureAwait(false);
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
                ? ApplicationActionContext.Create(operation, controlPlane.Serializer, body)
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

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

/// <summary>
/// A platform's own routes on an instance's control-plane port, tried before the control plane's.
/// </summary>
/// <param name="method">The request's HTTP method.</param>
/// <param name="segments">The request path's segments, still escaped.</param>
/// <param name="body">The request's body; empty for none.</param>
/// <returns>The answer, or <see langword="null"/> when the route is not the platform's.</returns>
internal delegate ValueTask<InstanceControlPlaneResponse?> InstanceControlPlaneRoutes(string method, string[] segments, ReadOnlyMemory<byte> body);

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
