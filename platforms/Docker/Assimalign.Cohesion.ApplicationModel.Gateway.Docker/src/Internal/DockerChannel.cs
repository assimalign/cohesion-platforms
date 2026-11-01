using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Docker;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// The channel between a network's instances and their gateway: it polls each running instance
/// container's control-plane port, published on a loopback port of the engine's host, and hands the
/// gateway each publication and renewal it finds there, then writes the gateway's answer back.
/// </summary>
/// <remarks>
/// <para>
/// The gateway pulls: there is no address both sides reach but the container's published port, and a
/// container cannot count on reaching a gateway that runs on the engine's host. A state whose answer
/// is not for the key its manifest names is a publication: the gateway's answer, or its refusal, is
/// posted back, where the instance waits for it. A state whose answer is current and whose renewal
/// count moved is a renewal; when the gateway does not know the instance, the channel clears the
/// answer, so the instance's next renewal finds itself unknown and it publishes again.
/// </para>
/// <para>
/// The channel lists the network's containers every <see cref="PollInterval"/>, so a gateway that
/// restarts reads every instance once: an instance an earlier gateway answered is a renewal, and the
/// new gateway keeps it or sends it to publish again. Containers are handled at the same time, each
/// on its own; a container that does not answer, such as one whose program has not opened its port
/// yet, is tried again on the next poll.
/// </para>
/// </remarks>
internal sealed class DockerChannel
{
    /// <summary>How often the channel polls the network's instances.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);

    private readonly IDockerEngine _engine;
    private readonly string _network;
    private readonly int _controlPlanePort;
    private readonly string _host;
    private readonly HttpClient _http;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private readonly ConcurrentDictionary<string, (string Key, long Renewal)> _renewals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _handling = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, Task>? _published;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _running;
    private Task _loop = Task.CompletedTask;

    /// <param name="engine">The engine that lists the network's containers.</param>
    /// <param name="network">The gateway's network.</param>
    /// <param name="controlPlanePort">The control-plane port inside each container.</param>
    /// <param name="host">Where the engine publishes container ports.</param>
    /// <param name="http">The client that reaches the published ports.</param>
    /// <param name="published">
    /// Called with a container's name once the gateway accepted its publication and the instance has
    /// the answer, such as to finish a rollout; a failure there is ignored.
    /// </param>
    public DockerChannel(IDockerEngine engine, string network, int controlPlanePort, string host, HttpClient http, Func<string, CancellationToken, Task>? published = null)
    {
        _engine = engine;
        _network = network;
        _controlPlanePort = controlPlanePort;
        _host = host;
        _http = http;
        _published = published;
    }

    public void Start(IApplicationInstancePublisher gateway)
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException(ErrorMessages.ChannelAlreadyStarted);
            }

            var running = new CancellationTokenSource();
            _running = running;
            _renewals.Clear();

            // Task.Run captures the caller's execution context: the gateway's.
            _loop = Task.Run(() => RunAsync(gateway, running.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? running;
        Task loop;
        lock (_lock)
        {
            running = _running;
            loop = _loop;
            _running = null;
        }

        if (running is null)
        {
            return;
        }

        await running.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);

        // The handlers still running were cancelled with the loop; none faults.
        await Task.WhenAll(_handling.Values).ConfigureAwait(false);
        _handling.Clear();
        running.Dispose();
    }

    private async Task RunAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(gateway, cancellationToken, wait: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Deliberately broad: an engine that does not answer a listing is listed again on the
                // next poll; it reports each failure in its own terms.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Polls every running instance container on the network once.
    /// </summary>
    /// <remarks>
    /// Each container is handled on its own, and one still handled from an earlier poll, such as one
    /// whose publication the gateway is still saving, is skipped, so a slow container never holds the
    /// others' renewals. The loop does not wait for the handlers (<paramref name="wait"/> is
    /// <see langword="false"/>); a test does.
    /// </remarks>
    internal async Task PollAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken, bool wait = true)
    {
        IReadOnlyList<DockerContainerSummary> containers = await _engine.ListContainersAsync(DockerWorkload.SelectNetwork(_network), cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var polls = new List<Task>();
        foreach (DockerContainerSummary container in containers)
        {
            if (DockerWorkload.GetName(container) is not { } name || container.State != "running" || DockerWorkload.GetHostPort(container, _controlPlanePort) is not { } port)
            {
                continue;
            }

            seen.Add(name);
            if (_handling.TryGetValue(name, out Task? handling) && !handling.IsCompleted)
            {
                continue;
            }

            Task poll = HandleSafelyAsync(gateway, name, port, cancellationToken);
            _handling[name] = poll;
            polls.Add(poll);
        }

        if (wait)
        {
            await Task.WhenAll(polls).ConfigureAwait(false);
        }

        foreach (KeyValuePair<string, Task> handling in _handling)
        {
            if (handling.Value.IsCompleted && !seen.Contains(handling.Key))
            {
                _handling.TryRemove(handling);
            }
        }

        // A container that is gone takes its renewal count with it.
        foreach (string name in _renewals.Keys)
        {
            if (!seen.Contains(name))
            {
                _renewals.TryRemove(name, out _);
            }
        }
    }

    // A handler never faults: whatever one container's exchange throws is that container's alone, and
    // it is read again on the next poll.
    private async Task HandleSafelyAsync(IApplicationInstancePublisher gateway, string container, int port, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(gateway, container, port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Deliberately broad, as the loop's own catch: the next poll reads the container again.
            _renewals.TryRemove(container, out _);
        }
    }

    private async Task HandleAsync(IApplicationInstancePublisher gateway, string container, int port, CancellationToken cancellationToken)
    {
        var channel = new Uri(string.Concat("http://", _host, ":", port.ToString(CultureInfo.InvariantCulture), "/", DockerProtocol.ChannelRoute));
        DockerChannelState? state = await ReadStateAsync(channel, cancellationToken).ConfigureAwait(false);
        if (state is null || state.RefusalKey == state.InstanceKey)
        {
            return;
        }

        ApplicationInstanceManifest? manifest;
        try
        {
            manifest = _json.Deserialize<ApplicationInstanceManifest>(Encoding.UTF8.GetBytes(state.Manifest));
        }
        catch (ApplicationContentException exception)
        {
            await AnswerAsync(channel, new DockerChannelAnswer(state.InstanceKey, null, exception.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (manifest is null)
        {
            return;
        }

        if (state.PublicationKey != state.InstanceKey)
        {
            await PublishAsync(gateway, container, channel, state, manifest, cancellationToken).ConfigureAwait(false);
            return;
        }

        // An answered state whose renewal count has not moved is the channel's own answer, or a renewal
        // already handled.
        if (_renewals.TryGetValue(container, out (string Key, long Renewal) last) && last == (state.InstanceKey, state.Renewal))
        {
            return;
        }

        _renewals[container] = (state.InstanceKey, state.Renewal);
        bool known;
        try
        {
            known = await gateway.RenewAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately broad: the gateway is not receiving, or refused the manifest. The renewal is
            // not recorded as handled, so the next poll hands it to the gateway again.
            _renewals.TryRemove(container, out _);
            return;
        }

        if (!known)
        {
            // The instance's next renewal finds no answer for itself, and it publishes again.
            await SendAsync(new HttpRequestMessage(HttpMethod.Delete, string.Concat(channel.AbsoluteUri, "/", DockerProtocol.AnswerRoute, "/", Uri.EscapeDataString(state.InstanceKey))), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(IApplicationInstancePublisher gateway, string container, Uri channel, DockerChannelState state, ApplicationInstanceManifest manifest, CancellationToken cancellationToken)
    {
        ApplicationInstancePublication publication;
        try
        {
            publication = await gateway.PublishAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately broad: whatever refused the publication is the instance's answer.
            await AnswerAsync(channel, new DockerChannelAnswer(state.InstanceKey, null, exception.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await AnswerAsync(channel, new DockerChannelAnswer(state.InstanceKey, Encoding.UTF8.GetString(_json.Serialize(publication).Span), null), cancellationToken).ConfigureAwait(false))
        {
            // The channel's own answer moves no renewal count, so it is not taken for a renewal.
            _renewals[container] = (state.InstanceKey, state.Renewal);
            if (_published is { } published)
            {
                try
                {
                    await published.Invoke(container, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Deliberately broad: what follows a publication is best effort, and never the
                    // instance's concern.
                }
            }
        }
    }

    private async Task<DockerChannelState?> ReadStateAsync(Uri channel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, channel);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize(body, DockerProtocolJsonContext.Default.DockerChannelState);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not listening yet, or not an instance of this platform: tried again on the next poll.
            return null;
        }
    }

    private Task<bool> AnswerAsync(Uri channel, DockerChannelAnswer answer, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, string.Concat(channel.AbsoluteUri, "/", DockerProtocol.AnswerRoute))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(answer, DockerProtocolJsonContext.Default.DockerChannelAnswer)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return SendAsync(request, cancellationToken);
    }

    // An answer that does not arrive is posted again: the instance still names an unanswered key on
    // the next poll.
    private async Task<bool> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return false;
            }
        }
    }
}
