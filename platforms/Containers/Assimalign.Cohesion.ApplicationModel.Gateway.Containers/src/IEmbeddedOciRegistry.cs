using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Serves a pull-only OCI Distribution Registry HTTP API v2 endpoint over loopback.
/// </summary>
public interface IEmbeddedOciRegistry : IAsyncDisposable
{
    /// <summary>
    /// Gets the loopback endpoint after <see cref="StartAsync"/> completes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The registry is not running.</exception>
    Uri Endpoint { get; }

    /// <summary>Starts accepting Registry HTTP API v2 requests.</summary>
    /// <param name="cancellationToken">Signals that startup should stop.</param>
    /// <returns>A task that completes when the loopback listener is active.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="SocketException">The loopback listener cannot bind or start.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the listener, cancels active requests, and waits for their completion.</summary>
    /// <param name="cancellationToken">Signals that waiting for shutdown should stop.</param>
    /// <returns>A task that completes when the registry has stopped.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task StopAsync(CancellationToken cancellationToken = default);
}
