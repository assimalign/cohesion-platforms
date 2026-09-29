using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerDaemonSmokeTests
{
    [DockerDaemonFact(DisplayName = "Cohesion Test [Docker] - Daemon smoke: Should ping an explicitly enabled reachable engine")]
    public async Task PingAsync_OnExplicitlyEnabledReachableDaemon_ShouldSucceed()
    {
        // Arrange
        Uri endpoint = DockerDaemonFactAttribute.ResolveEndpoint();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var engine = new DockerEngineClient(endpoint);

        // Act
        await engine.PingAsync(timeout.Token);

        // Assert: PingAsync returning is the Docker Engine compatibility assertion.
    }
}

internal sealed class DockerDaemonFactAttribute : FactAttribute
{
    private const string EnableVariable = "COHESION_DOCKER_DAEMON_SMOKE";

    public DockerDaemonFactAttribute()
    {
        string? enabled = Environment.GetEnvironmentVariable(EnableVariable);
        if (enabled is not "1"
            && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {EnableVariable}=1 to enable the real Docker-compatible daemon smoke test.";
            return;
        }

        Uri endpoint;
        try
        {
            endpoint = ResolveEndpoint();
        }
        catch (InvalidOperationException exception)
        {
            Skip = exception.Message;
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var engine = new DockerEngineClient(endpoint);
            engine.PingAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            Skip = $"Docker Engine '{endpoint}' is not reachable: {exception.Message}";
        }
        catch (IOException exception)
        {
            Skip = $"Docker Engine '{endpoint}' is not reachable: {exception.Message}";
        }
        catch (OperationCanceledException)
        {
            Skip = $"Docker Engine '{endpoint}' did not answer within three seconds.";
        }
    }

    internal static Uri ResolveEndpoint()
    {
        string? configured = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed))
            {
                throw new InvalidOperationException(
                    $"DOCKER_HOST '{configured}' is not an absolute Docker Engine URI.");
            }

            return parsed;
        }

        return OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine", UriKind.Absolute)
            : new Uri("unix:///var/run/docker.sock", UriKind.Absolute);
    }
}
