using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// Waits for what a cluster makes true in its own time, such as a second pod's instance publishing.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition.Invoke())
        {
            if (Stopwatch.GetElapsedTime(start) > timeout)
            {
                throw new TimeoutException($"The condition was not met within {timeout}.");
            }

            await Task.Delay(_interval);
        }
    }

    /// <summary>
    /// Attempts until an attempt returns, retrying the failures <paramref name="isTransient"/> accepts
    /// until the timeout, and throws the last of them after it.
    /// </summary>
    public static async Task<T> GetAsync<T>(Func<Task<T>> attempt, Func<Exception, bool> isTransient, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await attempt.Invoke();
            }
            catch (Exception exception) when (isTransient.Invoke(exception) && Stopwatch.GetElapsedTime(start) < timeout)
            {
                await Task.Delay(_interval);
            }
        }
    }
}
