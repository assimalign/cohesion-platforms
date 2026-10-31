using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// A gateway running until disposed, as one deployment of a gateway program runs it.
/// </summary>
internal sealed class GatewayRun : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private GatewayRun(ApplicationGateway gateway)
    {
        Gateway = gateway;
        _run = gateway.RunAsync(_stop.Token);
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>
    /// Runs the gateway and waits until its startup completes; a run that fails first throws its failure.
    /// </summary>
    public static async Task<GatewayRun> StartAsync(ApplicationGateway gateway, TimeSpan timeout)
    {
        var run = new GatewayRun(gateway);
        try
        {
            await gateway.Started.WaitAsync(timeout);
            return run;
        }
        catch
        {
            // Started already reported the run's failure, or the timeout: stop the run and rethrow it.
            await run._stop.CancelAsync();
            await Task.WhenAny(run._run);
            run._stop.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run;
        _stop.Dispose();
    }
}
