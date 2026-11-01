using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;

/// <summary>
/// Serves an instance's control plane on its pod's control-plane port, where the gateway reaches it
/// through the API server's pod proxy.
/// </summary>
/// <remarks>
/// <para>
/// Routes: <c>GET /manifest</c>; <c>GET /actions/{name}</c> reads an action and
/// <c>POST /actions/{name}</c> writes it with the body as its input; <c>PUT /state/{name}</c> restores
/// its state with the body as the snapshot's value. An action or a restore answers with its
/// <see cref="ApplicationActionResult"/>, written by the control plane's serializer, and a status:
/// 200 succeeded, 400 rejected, 404 no such action, 405 operation not accepted. Anything else is no
/// answer.
/// </para>
/// <para>
/// The port is the pod's own: only the cluster network and the API server's pod proxy reach it, so the
/// application's own listener and middleware never see a gateway's call. Each request runs in the
/// execution context the listener started in, the instance's.
/// </para>
/// </remarks>
internal sealed class KubernetesControlPlaneListener : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IApplicationInstanceControlPlane _controlPlane;
    private Task _accepting = Task.CompletedTask;
    private int _isStopped;

    private KubernetesControlPlaneListener(IApplicationInstanceControlPlane controlPlane)
    {
        _controlPlane = controlPlane;
    }

    public static KubernetesControlPlaneListener Start(IApplicationInstanceControlPlane controlPlane, int port)
    {
        var listener = new KubernetesControlPlaneListener(controlPlane);

        // Every address inside the pod; a Windows run outside a pod binds the loopback name only, which
        // needs no URL reservation.
        listener._listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://localhost:{port}/" : $"http://*:{port}/");
        listener._listener.Start();
        listener._accepting = Task.Run(listener.AcceptAsync);
        return listener;
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
            (int status, ReadOnlyMemory<byte> body) = await DispatchAsync(context.Request).ConfigureAwait(false);
            response.StatusCode = status;
            if (!body.IsEmpty)
            {
                response.ContentType = _controlPlane.Serializer.MediaType;
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
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

    private async Task<(int Status, ReadOnlyMemory<byte> Body)> DispatchAsync(HttpListenerRequest request)
    {
        string[] segments = request.Url?.AbsolutePath.Trim('/').Split('/') ?? [];
        if (segments is ["manifest"])
        {
            return request.HttpMethod == "GET"
                ? ((int)HttpStatusCode.OK, _controlPlane.Serializer.Serialize(_controlPlane.Manifest))
                : ((int)HttpStatusCode.MethodNotAllowed, ReadOnlyMemory<byte>.Empty);
        }

        if (segments is not [("actions" or "state") and string route, string name])
        {
            return ((int)HttpStatusCode.NotFound, ReadOnlyMemory<byte>.Empty);
        }

        if (!ActionName.TryCreate(Uri.UnescapeDataString(name), out ActionName action))
        {
            return Answer(ApplicationActionResult.NotFound());
        }

        byte[] body = await ReadBodyAsync(request).ConfigureAwait(false);
        ApplicationActionResult result;
        if (route == "state")
        {
            if (request.HttpMethod != "PUT")
            {
                return ((int)HttpStatusCode.MethodNotAllowed, ReadOnlyMemory<byte>.Empty);
            }

            result = await _controlPlane.RestoreAsync(ApplicationActionSnapshot.Create(action, body)).ConfigureAwait(false);
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
                return ((int)HttpStatusCode.MethodNotAllowed, ReadOnlyMemory<byte>.Empty);
            }

            ApplicationActionContext invocation = operation == ApplicationActionOperation.Write
                ? ApplicationActionContext.Create(operation, _controlPlane.Serializer, body)
                : ApplicationActionContext.Create(operation, _controlPlane.Serializer);
            result = await _controlPlane.ExecuteAsync(action, invocation).ConfigureAwait(false);
        }

        return Answer(result);
    }

    private (int Status, ReadOnlyMemory<byte> Body) Answer(ApplicationActionResult result)
    {
        int status = result.Status switch
        {
            ApplicationActionStatus.Succeeded => (int)HttpStatusCode.OK,
            ApplicationActionStatus.Rejected => (int)HttpStatusCode.BadRequest,
            ApplicationActionStatus.NotFound => (int)HttpStatusCode.NotFound,
            _ => (int)HttpStatusCode.MethodNotAllowed,
        };

        return (status, _controlPlane.Serializer.Serialize(result));
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
