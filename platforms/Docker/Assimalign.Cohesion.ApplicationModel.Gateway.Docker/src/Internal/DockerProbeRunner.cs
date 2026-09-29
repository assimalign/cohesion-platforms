using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerProbeRunner : IDisposable
{
    private readonly IDockerEngineClient _engine;
    private readonly DockerGatewayOptions _options;
    private readonly HttpClient _http = CreateHttpClient();

    public DockerProbeRunner(
        IDockerEngineClient engine,
        DockerGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        _engine = engine;
        _options = options;
    }

    public async Task<DockerProbeResult> RunAsync(
        string containerId,
        DockerProbePlan probe,
        DockerContainerInspectResponse inspection,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(inspection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeTimeout);

        try
        {
            return probe.Kind switch
            {
                ProbeKind.None => DockerProbeResult.Success(),
                ProbeKind.Http => await RunHttpAsync(probe, inspection, timeout.Token)
                    .ConfigureAwait(false),
                ProbeKind.Tcp => await RunTcpAsync(probe, inspection, timeout.Token)
                    .ConfigureAwait(false),
                ProbeKind.Exec => await RunExecAsync(containerId, probe, timeout.Token)
                    .ConfigureAwait(false),
                _ => DockerProbeResult.Failure(
                    $"Unsupported Docker probe kind '{probe.Kind}'.",
                    failFast: true),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} probe exceeded {_options.ProbeTimeout}.");
        }
        catch (HttpRequestException exception)
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} HTTP probe failed: {exception.Message}");
        }
        catch (SocketException exception)
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} TCP probe failed: {exception.Message}");
        }
    }

    public void Dispose() => _http.Dispose();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // HTTPS health probes are loopback-only Docker bindings. Match Kubernetes probe
                // semantics by checking reachability/status without requiring the gateway host to
                // trust a certificate issued for the container's service or public DNS name.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private async Task<DockerProbeResult> RunHttpAsync(
        DockerProbePlan probe,
        DockerContainerInspectResponse inspection,
        CancellationToken cancellationToken)
    {
        int port = FindLoopbackPort(probe, inspection);
        string path = string.IsNullOrWhiteSpace(probe.Value) ? "/" : probe.Value;
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} HTTP probe path '{path}' is not absolute.",
                failFast: true);
        }

        string scheme = string.Equals(probe.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";
        var address = new UriBuilder(scheme, IPAddress.Loopback.ToString(), port, path).Uri;
        using HttpResponseMessage response = await _http
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK)
        {
            return DockerProbeResult.Success();
        }

        bool failFast = response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed;
        return DockerProbeResult.Failure(
            $"Docker {probe.Role} HTTP probe returned {(int)response.StatusCode} ({response.StatusCode}).",
            failFast);
    }

    private static async Task<DockerProbeResult> RunTcpAsync(
        DockerProbePlan probe,
        DockerContainerInspectResponse inspection,
        CancellationToken cancellationToken)
    {
        int port = FindLoopbackPort(probe, inspection);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        return DockerProbeResult.Success();
    }

    private async Task<DockerProbeResult> RunExecAsync(
        string containerId,
        DockerProbePlan probe,
        CancellationToken cancellationToken)
    {
        DockerExecCreateResponse created = await _engine
            .CreateExecAsync(
                containerId,
                new DockerExecCreateRequest
                {
                    AttachStdout = false,
                    AttachStderr = false,
                    Cmd = [.. probe.Command],
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.Id))
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} exec probe returned no exec ID.",
                failFast: true);
        }

        await _engine
            .StartExecAsync(
                created.Id,
                new DockerExecStartRequest { Detach = false, Tty = false },
                cancellationToken)
            .ConfigureAwait(false);
        DockerExecInspectResponse? result = await _engine
            .InspectExecAsync(created.Id, cancellationToken)
            .ConfigureAwait(false);
        if (result is null || result.Running)
        {
            return DockerProbeResult.Failure(
                $"Docker {probe.Role} exec probe did not produce a completed result.");
        }

        return result.ExitCode == 0
            ? DockerProbeResult.Success()
            : DockerProbeResult.Failure(
                $"Docker {probe.Role} exec probe exited with code " +
                result.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static int FindLoopbackPort(
        DockerProbePlan probe,
        DockerContainerInspectResponse inspection)
    {
        if (probe.Endpoint is null)
        {
            throw new InvalidOperationException(
                $"Docker {probe.Role} {probe.Kind} probe has no endpoint.");
        }

        Dictionary<string, DockerPortBinding[]?>? ports = inspection.NetworkSettings?.Ports;
        if (ports is null)
        {
            throw new InvalidOperationException(
                $"Docker container has no observed port bindings for {probe.Role} probe endpoint '{probe.Endpoint}'.");
        }

        if (probe.ContainerPort is null || probe.Protocol is null)
        {
            throw new InvalidOperationException(
                $"Docker {probe.Role} {probe.Kind} probe has no compiled container port.");
        }

        string key = probe.ContainerPort.Value.ToString(CultureInfo.InvariantCulture)
            + "/" + probe.Protocol.ToLowerInvariant();
        if (!ports.TryGetValue(key, out DockerPortBinding[]? bindings) || bindings is null)
        {
            throw new InvalidOperationException(
                $"Docker container has no observed binding '{key}' for {probe.Role} probe endpoint '{probe.Endpoint}'.");
        }

        for (int index = 0; index < bindings.Length; index++)
        {
            DockerPortBinding binding = bindings[index];
            if (IsLoopback(binding.HostIp)
                && int.TryParse(
                    binding.HostPort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int port)
                && port is >= 1 and <= 65535)
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"Docker container has no loopback binding for {probe.Role} probe endpoint '{probe.Endpoint}'.");
    }

    private static bool IsLoopback(string? host) =>
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct DockerProbeResult(
    bool Succeeded,
    bool FailFast,
    string? Detail)
{
    public static DockerProbeResult Success() => new(true, false, null);

    public static DockerProbeResult Failure(string detail, bool failFast = false) =>
        new(false, failFast, detail);
}
