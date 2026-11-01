using System;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// Where the gateway reaches the engine's API, resolved as the docker CLI resolves it: the options'
/// endpoint, then the <c>DOCKER_HOST</c> variable, then the platform's default socket.
/// </summary>
/// <remarks>
/// The forms are the docker CLI's: <c>unix:///var/run/docker.sock</c>,
/// <c>npipe:////./pipe/docker_engine</c> (also <c>npipe://./pipe/docker_engine</c>), and
/// <c>tcp://host:2375</c>, or <c>http://</c> for the same. Podman serves Docker's API on the same
/// defaults: on Linux at its own socket (set <c>DOCKER_HOST=unix:///run/podman/podman.sock</c>), and
/// on Windows through the machine's <c>docker_engine</c> pipe.
/// </remarks>
internal sealed class DockerEngineEndpoint
{
    /// <summary>The variable the docker CLI reads its engine from.</summary>
    public const string HostVariable = "DOCKER_HOST";

    /// <summary>The variable that turns TLS on for a <c>tcp://</c> engine, which this platform does not speak yet.</summary>
    public const string TlsVerifyVariable = "DOCKER_TLS_VERIFY";

    /// <summary>The engine every Windows Docker or Podman machine serves.</summary>
    public const string WindowsDefault = "npipe:////./pipe/docker_engine";

    /// <summary>The engine every Linux and macOS Docker serves.</summary>
    public const string UnixDefault = "unix:///var/run/docker.sock";

    private DockerEngineEndpoint(string value, DockerEngineTransport transport, string address, string? server)
    {
        Value = value;
        Transport = transport;
        Address = address;
        Server = server;
    }

    /// <summary>Gets the endpoint as it was given.</summary>
    public string Value { get; }

    /// <summary>Gets how the endpoint is reached.</summary>
    public DockerEngineTransport Transport { get; }

    /// <summary>Gets the socket path, the pipe name, or the HTTP base address.</summary>
    public string Address { get; }

    /// <summary>Gets the named pipe's server, <c>.</c> for this machine; <see langword="null"/> for other transports.</summary>
    public string? Server { get; }

    /// <summary>
    /// Resolves the endpoint: the configured one, <c>DOCKER_HOST</c>, then the platform's default.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not of a form the platform speaks.</exception>
    /// <exception cref="NotSupportedException">The endpoint asks for TLS or SSH, which the platform does not speak yet.</exception>
    public static DockerEngineEndpoint Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Parse(configured);
        }

        if (Environment.GetEnvironmentVariable(HostVariable) is { Length: > 0 } host)
        {
            return Parse(host);
        }

        return Parse(OperatingSystem.IsWindows() ? WindowsDefault : UnixDefault);
    }

    /// <summary>
    /// Parses an endpoint of one of the docker CLI's forms.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not of a form the platform speaks.</exception>
    /// <exception cref="NotSupportedException">The endpoint asks for TLS or SSH, which the platform does not speak yet.</exception>
    public static DockerEngineEndpoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();
        int colon = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new ArgumentException(Format(ErrorMessages.EngineEndpointInvalid, value), nameof(value));
        }

        string scheme = trimmed[..colon].ToLowerInvariant();
        string rest = trimmed[(colon + 3)..];
        switch (scheme)
        {
            case "unix":
                // unix:///var/run/docker.sock: the path follows an empty authority.
                if (rest.Length < 2 || rest[0] != '/')
                {
                    throw new ArgumentException(Format(ErrorMessages.EngineEndpointInvalid, value), nameof(value));
                }

                return new DockerEngineEndpoint(value, DockerEngineTransport.UnixSocket, Uri.UnescapeDataString(rest), null);

            case "npipe":
                return ParsePipe(value, rest);

            case "tcp":
            case "http":
                if (Environment.GetEnvironmentVariable(TlsVerifyVariable) is { Length: > 0 } verify && verify != "0")
                {
                    throw new NotSupportedException(Format(ErrorMessages.EngineTlsUnsupported, value));
                }

                if (!Uri.TryCreate(string.Concat("http://", rest), UriKind.Absolute, out Uri? http) || http.Host.Length == 0)
                {
                    throw new ArgumentException(Format(ErrorMessages.EngineEndpointInvalid, value), nameof(value));
                }

                return new DockerEngineEndpoint(value, DockerEngineTransport.Http, new UriBuilder(http) { Path = "/", Query = string.Empty }.Uri.AbsoluteUri, null);

            case "https":
            case "ssh":
                throw new NotSupportedException(Format(ErrorMessages.EngineTlsUnsupported, value));

            default:
                throw new ArgumentException(Format(ErrorMessages.EngineEndpointInvalid, value), nameof(value));
        }
    }

    // npipe:////./pipe/docker_engine and npipe://./pipe/docker_engine name the same pipe; so does a
    // server other than '.', as npipe:////server/pipe/name.
    private static DockerEngineEndpoint ParsePipe(string value, string rest)
    {
        string[] segments = rest.Replace('\\', '/').Trim('/').Split('/');
        if (segments.Length != 3 || segments[0].Length == 0 || !string.Equals(segments[1], "pipe", StringComparison.OrdinalIgnoreCase) || segments[2].Length == 0)
        {
            throw new ArgumentException(Format(ErrorMessages.EngineEndpointInvalid, value), nameof(value));
        }

        return new DockerEngineEndpoint(value, DockerEngineTransport.NamedPipe, Uri.UnescapeDataString(segments[2]), segments[0]);
    }

    public override string ToString()
    {
        return Value;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}

/// <summary>
/// How an engine endpoint is reached.
/// </summary>
internal enum DockerEngineTransport
{
    /// <summary>A Unix domain socket.</summary>
    UnixSocket,

    /// <summary>A Windows named pipe.</summary>
    NamedPipe,

    /// <summary>Plain HTTP over TCP.</summary>
    Http,
}
