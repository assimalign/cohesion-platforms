using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// A gateway running until disposed, as one deployment of a gateway program runs it.
/// </summary>
internal sealed class GatewayRun : IAsyncDisposable
{
    private GatewayRun(ApplicationGateway gateway)
    {
        Gateway = gateway;
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>
    /// Starts the gateway and waits until its startup completes; a run that fails first throws its failure.
    /// </summary>
    public static async Task<GatewayRun> StartAsync(ApplicationGateway gateway, TimeSpan timeout)
    {
        try
        {
            await gateway.StartAsync().WaitAsync(timeout);
            return new GatewayRun(gateway);
        }
        catch
        {
            // Startup already reported the run's failure, or the timeout: stop the run, release the
            // platform the gateway owns, and rethrow it.
            await gateway.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the run and disposes the gateway; a run that failed while it ran throws its failure.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Gateway.StopAsync();
            await Gateway.Completion;
        }
        finally
        {
            await Gateway.DisposeAsync();
        }
    }
}
