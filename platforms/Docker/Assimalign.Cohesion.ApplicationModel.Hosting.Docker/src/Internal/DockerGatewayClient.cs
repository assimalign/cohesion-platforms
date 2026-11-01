using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Docker;
using Assimalign.Cohesion.ApplicationModel.Hosting.Platforms;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Internal;

/// <summary>
/// The Docker platform's gateway client. It keeps the instance's side of the publication channel on
/// the container's control-plane port, where the gateway polls it, and serves the control plane on
/// the same port.
/// </summary>
/// <remarks>
/// <para>
/// The gateway pulls, because a container cannot count on reaching a gateway that runs on the
/// engine's host. Publishing opens the port, sets the channel's state to the manifest and the
/// instance's key, and waits for the gateway to answer it there. The manifest names the container
/// and its control-plane port, which the gateway finds published on its host, as its control plane's
/// address.
/// </para>
/// <para>
/// A renewal moves the state's renewal count while the state still carries the gateway's answer for
/// this instance; the gateway renews the registration when it sees the count move. A gateway that no
/// longer knows the instance clears its answer, so the next renewal reports it unknown and the
/// instance publishes again.
/// </para>
/// </remarks>
internal sealed class DockerGatewayClient : IApplicationInstanceGatewayClient
{
    private static readonly TimeSpan _answerTimeout = TimeSpan.FromSeconds(60);

    private readonly DockerInstanceEnvironment _environment;
    private readonly JsonApplicationContentSerializer _json = JsonApplicationContentSerializer.Create();
    private readonly Lock _lock = new();
    private InstanceControlPlaneListener? _listener;
    private DockerChannelState? _state;
    private TaskCompletionSource<DockerChannelAnswer>? _answer;
    private bool _serving;

    public DockerGatewayClient(DockerInstanceEnvironment environment)
    {
        _environment = environment;
    }

    public PlatformName Platform => DockerProtocol.PlatformName;

    /// <exception cref="InvalidOperationException">
    /// The environment names no gateway endpoint or container, the gateway refused the publication, or
    /// no gateway answered it in time.
    /// </exception>
    public async ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        (string network, string container) = ResolveContainer();
        ApplicationInstanceManifest published = manifest with
        {
            ControlPlaneEndpoint = DockerProtocol.CreateControlPlaneEndpoint(network, container, _environment.ControlPlanePort),
        };

        var answer = new TaskCompletionSource<DockerChannelAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            // A publication clears an earlier answer, so the gateway answers again.
            _state = new DockerChannelState(manifest.Key.ToString(), Encoding.UTF8.GetString(_json.Serialize(published).Span), null, null, 0);
            _answer?.TrySetCanceled(CancellationToken.None);
            _answer = answer;
            _listener ??= InstanceControlPlaneListener.Start(_environment.ControlPlanePort, routes: RouteAsync);
        }

        DockerChannelAnswer received;
        try
        {
            received = await answer.Task.WaitAsync(_answerTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PublicationUnanswered, network, _answerTimeout));
        }

        if (received.Refusal is { } reason)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PublicationRefused, reason));
        }

        return received.Publication is { } publication
            ? _json.Deserialize<ApplicationInstancePublication>(Encoding.UTF8.GetBytes(publication)) ?? throw new InvalidOperationException(ErrorMessages.PublicationEmpty)
            : throw new InvalidOperationException(ErrorMessages.PublicationEmpty);
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_lock)
        {
            if (_state is null || _state.PublicationKey != manifest.Key.ToString())
            {
                return ValueTask.FromResult(false);
            }

            _state = _state with { Renewal = _state.Renewal + 1 };
            return ValueTask.FromResult(true);
        }
    }

    /// <exception cref="InvalidOperationException">The client already serves a control plane.</exception>
    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        lock (_lock)
        {
            if (_serving)
            {
                throw new InvalidOperationException(ErrorMessages.ClientAlreadyStarted);
            }

            _listener ??= InstanceControlPlaneListener.Start(_environment.ControlPlanePort, routes: RouteAsync);
            _listener.Serve(controlPlane);
            _serving = true;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        InstanceControlPlaneListener? listener;
        lock (_lock)
        {
            listener = _listener;
            _listener = null;
            _serving = false;
            _state = null;

            // A publication still waiting ends now: nothing answers a stopped client.
            _answer?.TrySetCanceled(CancellationToken.None);
            _answer = null;
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The instance's side of the channel: the gateway reads the state, answers the publication it
    // names, and clears its answer for an instance it does not know.
    // Only the answer route reads a body.
    private async ValueTask<InstanceControlPlaneResponse?> RouteAsync(string method, string[] segments, Func<ValueTask<ReadOnlyMemory<byte>>> readBody)
    {
        if (segments is [DockerProtocol.ChannelRoute])
        {
            return method == "GET" ? ReadState() : InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
        }

        if (segments is [DockerProtocol.ChannelRoute, DockerProtocol.AnswerRoute])
        {
            return method == "POST" ? Answer(await readBody().ConfigureAwait(false)) : InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
        }

        if (segments is [DockerProtocol.ChannelRoute, DockerProtocol.AnswerRoute, string key])
        {
            return method == "DELETE" ? Clear(Uri.UnescapeDataString(key)) : InstanceControlPlaneResponse.Empty(HttpStatusCode.MethodNotAllowed);
        }

        return null;
    }

    private InstanceControlPlaneResponse ReadState()
    {
        DockerChannelState? state;
        lock (_lock)
        {
            state = _state;
        }

        return state is null
            ? InstanceControlPlaneResponse.Empty(HttpStatusCode.NotFound)
            : new InstanceControlPlaneResponse((int)HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(state, DockerProtocolJsonContext.Default.DockerChannelState), "application/json");
    }

    private InstanceControlPlaneResponse Answer(ReadOnlyMemory<byte> body)
    {
        DockerChannelAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize(body.Span, DockerProtocolJsonContext.Default.DockerChannelAnswer);
        }
        catch (JsonException)
        {
            return InstanceControlPlaneResponse.Empty(HttpStatusCode.BadRequest);
        }

        if (answer is null || (answer.Publication is null) == (answer.Refusal is null))
        {
            return InstanceControlPlaneResponse.Empty(HttpStatusCode.BadRequest);
        }

        TaskCompletionSource<DockerChannelAnswer>? waiting;
        lock (_lock)
        {
            // An answer for a key the instance no longer publishes is stale.
            if (_state is null || _state.InstanceKey != answer.InstanceKey)
            {
                return InstanceControlPlaneResponse.Empty(HttpStatusCode.Conflict);
            }

            _state = answer.Publication is not null
                ? _state with { PublicationKey = answer.InstanceKey, RefusalKey = null }
                : _state with { PublicationKey = null, RefusalKey = answer.InstanceKey };
            waiting = _answer;
            _answer = null;
        }

        waiting?.TrySetResult(answer);
        return InstanceControlPlaneResponse.Empty(HttpStatusCode.NoContent);
    }

    private InstanceControlPlaneResponse Clear(string key)
    {
        lock (_lock)
        {
            if (_state is not null && _state.PublicationKey == key)
            {
                _state = _state with { PublicationKey = null };
            }
        }

        return InstanceControlPlaneResponse.Empty(HttpStatusCode.NoContent);
    }

    private (string Network, string Container) ResolveContainer()
    {
        if (!DockerProtocol.TryParseGatewayEndpoint(_environment.GatewayEndpoint is { } endpoint && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? uri : null, out string? network))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.GatewayEndpointInvalid, AppEnvironment.Variables.ManifestEndpoint, _environment.GatewayEndpoint));
        }

        if (string.IsNullOrWhiteSpace(_environment.ContainerName))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ContainerNameMissing, DockerProtocol.ContainerNameVariable));
        }

        return (network, _environment.ContainerName);
    }
}
