using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Options controlling Docker Engine connection, image acquisition, observation, probes, and
/// supervised restart behavior for <see cref="DockerGateway"/>.
/// </summary>
public sealed class DockerGatewayOptions : ApplicationGatewayOptions
{
    /// <summary>
    /// Gets or sets the Docker Engine endpoint. Supported schemes are <c>http</c>, <c>https</c>,
    /// <c>unix</c>, and <c>npipe</c>. When omitted, <c>DOCKER_HOST</c> is honored before the
    /// operating-system default socket is selected.
    /// </summary>
    public Uri? EngineEndpoint { get; set; }

    /// <summary>
    /// Gets or sets a custom image realizer. The default verifies an existing engine image by
    /// digest or loads a configured OCI archive and verifies the resulting engine image.
    /// </summary>
    public IImageRealizer? ImageRealizer { get; set; }

    /// <summary>
    /// Gets digest-pinned image-reference to OCI archive-path mappings used by the default image
    /// realizer. This is the narrow archive bridge used until the shared image index lands.
    /// </summary>
    public IDictionary<string, string> ImageArchives { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the host written to observed public endpoint addresses. Defaults to
    /// <c>localhost</c>.
    /// </summary>
    public string PublicHost { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the sink for compiler warnings. Unknown hint keys are warned once per
    /// gateway session. Defaults to standard error.
    /// </summary>
    public Action<string> WarningHandler { get; set; } = Console.Error.WriteLine;

    /// <summary>Gets or sets the full-inspection interval. Defaults to two seconds.</summary>
    public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets the interval between unsuccessful probe attempts. Defaults to one second.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the maximum duration of one probe attempt. Defaults to five seconds.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the consecutive liveness-failure count that requests a restart. Defaults to
    /// three; the first failure is still observed immediately as <c>Degraded</c>.
    /// </summary>
    public int LivenessFailureThreshold { get; set; } = 3;

    /// <summary>Gets or sets the first supervised restart delay. Defaults to one second.</summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the maximum supervised restart delay. Defaults to 30 seconds.</summary>
    public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the maximum supervised restart attempts. Defaults to five.</summary>
    public int MaximumRestartAttempts { get; set; } = 5;

    /// <summary>Gets or sets the event-stream reconnect delay. Defaults to one second.</summary>
    public TimeSpan EventReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    internal void ValidateDocker()
    {
        if (EngineEndpoint is not null && !EngineEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("EngineEndpoint must be an absolute URI.", nameof(EngineEndpoint));
        }

        if (EngineEndpoint is not null
            && EngineEndpoint.Scheme is not "http" and not "https" and not "unix" and not "npipe")
        {
            throw new NotSupportedException(
                $"Docker Engine endpoint scheme '{EngineEndpoint.Scheme}' is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PublicHost);
        _ = Uri.CreateEndpoint("http", PublicHost, 80);
        ArgumentNullException.ThrowIfNull(WarningHandler);
        RequirePositive(ObservationInterval, nameof(ObservationInterval));
        RequirePositive(ProbeInterval, nameof(ProbeInterval));
        RequirePositive(ProbeTimeout, nameof(ProbeTimeout));
        RequirePositive(EventReconnectDelay, nameof(EventReconnectDelay));
        if (LivenessFailureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LivenessFailureThreshold),
                "LivenessFailureThreshold must be greater than zero.");
        }

        if (InitialRestartBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialRestartBackoff),
                "InitialRestartBackoff must be zero or greater.");
        }

        if (MaximumRestartBackoff < InitialRestartBackoff)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRestartBackoff),
                "MaximumRestartBackoff must be at least InitialRestartBackoff.");
        }

        if (MaximumRestartAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRestartAttempts),
                "MaximumRestartAttempts must be zero or greater.");
        }

        foreach ((string image, string archive) in ImageArchives)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(image);
            ArgumentException.ThrowIfNullOrWhiteSpace(archive);
            _ = ContainerImageArtifacts.Create(default, image);
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be greater than zero.");
        }
    }
}
