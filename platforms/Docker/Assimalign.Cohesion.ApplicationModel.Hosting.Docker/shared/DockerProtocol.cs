using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Docker;

/// <summary>
/// The protocol the two halves of the Docker platform share: the variables through which the gateway
/// tells a container who it is, the forms of the platform's endpoints, and the channel routes and
/// documents an instance serves on its control-plane port.
/// </summary>
/// <remarks>
/// <para>
/// Both halves compile this file, so neither references the other: the instance half from its own
/// <c>shared</c> folder, and the gateway half by a link to it.
/// </para>
/// <para>
/// The gateway pulls: there is no address both sides reach but the instance's control-plane port,
/// which the engine publishes on a loopback port of its host. An instance keeps its side of the
/// channel, the <see cref="DockerChannelState"/>, on that port; the gateway reads it
/// (<c>GET /channel</c>), answers a publication or refuses it (<c>POST /channel/answer</c>), and
/// clears its answer for an instance it does not know (<c>DELETE /channel/answer/{key}</c>), so the instance
/// publishes again. The gateway's calls, reads, writes, and restores, go over the same port.
/// </para>
/// </remarks>
internal static class DockerProtocol
{
    /// <summary>The platform's name.</summary>
    public const string PlatformName = "docker";

    /// <summary>The scheme of the platform's endpoints.</summary>
    public const string Scheme = "docker";

    /// <summary>The variable the platform sets to the name of the instance's container.</summary>
    public const string ContainerNameVariable = "DOCKER_CONTAINER_NAME";

    /// <summary>The variable the platform sets to the port the instance serves its control plane on.</summary>
    public const string ControlPlanePortVariable = "DOCKER_CONTROL_PLANE_PORT";

    /// <summary>The port an instance serves its control plane on unless its environment names another.</summary>
    public const int DefaultControlPlanePort = 9090;

    /// <summary>The route of an instance's channel state.</summary>
    public const string ChannelRoute = "channel";

    /// <summary>The route of the gateway's answer to an instance's publication, under <see cref="ChannelRoute"/>.</summary>
    public const string AnswerRoute = "answer";

    /// <summary>
    /// Creates the endpoint an instance reaches its gateway at: the network whose containers the
    /// gateway runs and polls, <c>docker:///networks/{network}</c>.
    /// </summary>
    public static Uri CreateGatewayEndpoint(string network)
    {
        return new Uri(string.Concat(Scheme, ":///networks/", network));
    }

    /// <summary>
    /// Reads the network from a gateway endpoint.
    /// </summary>
    public static bool TryParseGatewayEndpoint(Uri? endpoint, [NotNullWhen(true)] out string? network)
    {
        network = null;
        if (endpoint is null || !endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = endpoint.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments[0] != "networks" || segments[1].Length == 0)
        {
            return false;
        }

        network = segments[1];
        return true;
    }

    /// <summary>
    /// Creates the endpoint the gateway reaches an instance's control plane at: the container and the
    /// port inside it, which the gateway finds published on its host,
    /// <c>docker:///networks/{network}/containers/{container}:{port}/</c>.
    /// </summary>
    public static Uri CreateControlPlaneEndpoint(string network, string container, int port)
    {
        return new Uri(string.Concat(Scheme, ":///networks/", network, "/containers/", container, ":", port.ToString(CultureInfo.InvariantCulture), "/"));
    }

    /// <summary>
    /// Reads the network, the container, and the port from a control-plane endpoint.
    /// </summary>
    public static bool TryParseControlPlaneEndpoint(Uri? endpoint, [NotNullWhen(true)] out string? network, [NotNullWhen(true)] out string? container, out int port)
    {
        network = null;
        container = null;
        port = 0;
        if (endpoint is null || !endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = endpoint.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[0] != "networks" || segments[2] != "containers")
        {
            return false;
        }

        int colon = segments[3].LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(segments[3].AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
        {
            return false;
        }

        network = segments[1];
        container = segments[3][..colon];
        return network.Length > 0;
    }
}

/// <summary>
/// An instance's side of the channel, as <c>GET /channel</c> answers it: the manifest it published and
/// whether the gateway answered it.
/// </summary>
/// <param name="InstanceKey">The key of the instance that published the manifest.</param>
/// <param name="Manifest">The manifest, as the protocol's JSON.</param>
/// <param name="PublicationKey">The instance key the gateway's publication answers; <see langword="null"/> until it answers.</param>
/// <param name="RefusalKey">The instance key the gateway refused; <see langword="null"/> unless it refused.</param>
/// <param name="Renewal">
/// How many times the instance renewed since it published: a renewal moves it, and the gateway renews
/// the instance's registration when it sees it move.
/// </param>
internal sealed record DockerChannelState(string InstanceKey, string Manifest, string? PublicationKey, string? RefusalKey, long Renewal);

/// <summary>
/// The gateway's answer to a publication, as <c>POST /channel/answer</c> carries it: the publication, or
/// the refusal's reason.
/// </summary>
/// <param name="InstanceKey">The instance key the answer is for; an instance that published again since ignores it.</param>
/// <param name="Publication">The publication, as the protocol's JSON; <see langword="null"/> for a refusal.</param>
/// <param name="Refusal">Why the gateway refused the publication; <see langword="null"/> for a publication.</param>
internal sealed record DockerChannelAnswer(string InstanceKey, string? Publication, string? Refusal);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DockerChannelState))]
[JsonSerializable(typeof(DockerChannelAnswer))]
internal sealed partial class DockerProtocolJsonContext : JsonSerializerContext
{
}
