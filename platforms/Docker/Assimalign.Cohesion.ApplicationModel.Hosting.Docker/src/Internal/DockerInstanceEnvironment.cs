using System.Globalization;

using Assimalign.Cohesion.ApplicationModel.Docker;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Internal;

/// <summary>
/// What the Docker platform hands an instance's container: the endpoint of its gateway, the
/// container's own name, and the port it serves its control plane on.
/// </summary>
/// <param name="GatewayEndpoint">The gateway's endpoint, <c>docker:///networks/{network}</c>.</param>
/// <param name="ContainerName">The container's name, which the gateway finds it by.</param>
/// <param name="ControlPlanePort">The port inside the container the control plane serves on.</param>
internal sealed record DockerInstanceEnvironment(string? GatewayEndpoint, string? ContainerName, int ControlPlanePort)
{
    // Read through AppEnvironment, as every resource reads its environment, so a test can hand a
    // program its variables in a scope of its own.
    public static DockerInstanceEnvironment Read()
    {
        string? port = AppEnvironment.GetValue(DockerProtocol.ControlPlanePortVariable);
        return new DockerInstanceEnvironment(
            AppEnvironment.GetValue(AppEnvironment.Variables.ManifestEndpoint),
            AppEnvironment.GetValue(DockerProtocol.ContainerNameVariable),
            int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is > 0 and <= 65535
                ? value
                : DockerProtocol.DefaultControlPlanePort);
    }
}
