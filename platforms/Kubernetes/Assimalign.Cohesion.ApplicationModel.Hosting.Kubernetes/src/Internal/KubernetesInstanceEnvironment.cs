using System.Globalization;

using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;

/// <summary>
/// What the Kubernetes platform hands an instance's pod: the endpoint of its gateway, the pod's own
/// identity through the downward API, and the port it serves its control plane on.
/// </summary>
/// <param name="GatewayEndpoint">The gateway's endpoint, <c>kubernetes:///namespaces/{namespace}</c>.</param>
/// <param name="PodName">The pod's name, which names its Lease.</param>
/// <param name="PodNamespace">The pod's namespace.</param>
/// <param name="PodUid">The pod's UID, which owns its Lease so the Lease goes with the pod.</param>
/// <param name="ControlPlanePort">The port the control plane serves on.</param>
internal sealed record KubernetesInstanceEnvironment(
    string? GatewayEndpoint,
    string? PodName,
    string? PodNamespace,
    string? PodUid,
    int ControlPlanePort)
{
    // Read through AppEnvironment, as every resource reads its environment, so a test can hand a
    // program its variables in a scope of its own.
    public static KubernetesInstanceEnvironment Read()
    {
        string? port = AppEnvironment.GetValue(KubernetesProtocol.ControlPlanePortVariable);
        return new KubernetesInstanceEnvironment(
            AppEnvironment.GetValue(AppEnvironment.Variables.ManifestEndpoint),
            AppEnvironment.GetValue(KubernetesProtocol.PodNameVariable),
            AppEnvironment.GetValue(KubernetesProtocol.PodNamespaceVariable),
            AppEnvironment.GetValue(KubernetesProtocol.PodUidVariable),
            int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is > 0 and <= 65535
                ? value
                : KubernetesProtocol.DefaultControlPlanePort);
    }
}
