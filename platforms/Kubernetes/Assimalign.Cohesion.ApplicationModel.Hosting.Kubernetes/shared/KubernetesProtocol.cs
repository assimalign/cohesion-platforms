using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Kubernetes;

/// <summary>
/// The protocol the two halves of the Kubernetes platform share: the names of the objects, labels,
/// annotations, and variables through which an instance and its gateway reach each other through the
/// cluster's API server, and the forms of the platform's endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Both halves compile this file, so neither references the other: the instance half from its own
/// <c>shared</c> folder, and the gateway half by a link to it.
/// </para>
/// <para>
/// An instance publishes and renews through a <c>coordination.k8s.io/v1</c> Lease named after its pod,
/// as a kubelet heartbeats through its node's Lease: the manifest is an annotation, the renewal is the
/// Lease's renew time, and the gateway writes its answer back as annotations. The gateway calls an
/// instance through the API server's pod proxy, at the control-plane port of the instance's pod.
/// </para>
/// </remarks>
internal static class KubernetesProtocol
{
    /// <summary>The platform's name.</summary>
    public const string PlatformName = "kubernetes";

    /// <summary>The scheme of the platform's endpoints.</summary>
    public const string Scheme = "kubernetes";

    /// <summary>The label every object the platform creates carries, with <see cref="ManagedByValue"/>.</summary>
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";

    /// <summary>The value of <see cref="ManagedByLabel"/>.</summary>
    public const string ManagedByValue = "cohesion";

    /// <summary>The label that tells the platform's objects apart by role, such as <see cref="InstanceRole"/>.</summary>
    public const string RoleLabel = "cohesion.assimalign.com/role";

    /// <summary>The role of an instance's Lease.</summary>
    public const string InstanceRole = "instance";

    /// <summary>The label selector of every instance Lease.</summary>
    public const string InstanceLeaseSelector = ManagedByLabel + "=" + ManagedByValue + "," + RoleLabel + "=" + InstanceRole;

    /// <summary>The annotation that holds the manifest an instance published, as JSON.</summary>
    public const string ManifestAnnotation = "cohesion.assimalign.com/manifest";

    /// <summary>The annotation that holds the key of the instance that published the manifest.</summary>
    public const string InstanceKeyAnnotation = "cohesion.assimalign.com/instance-key";

    /// <summary>The annotation in which the gateway answers a publication, as JSON.</summary>
    public const string PublicationAnnotation = "cohesion.assimalign.com/publication";

    /// <summary>The annotation that names the instance key the gateway's answer is for.</summary>
    public const string PublicationKeyAnnotation = "cohesion.assimalign.com/publication-key";

    /// <summary>The annotation in which the gateway refuses a publication, with its reason.</summary>
    public const string RefusalAnnotation = "cohesion.assimalign.com/refusal";

    /// <summary>The annotation that names the instance key the gateway's refusal is for.</summary>
    public const string RefusalKeyAnnotation = "cohesion.assimalign.com/refusal-key";

    /// <summary>The variable the platform sets to the name of the instance's pod.</summary>
    public const string PodNameVariable = "KUBERNETES_POD_NAME";

    /// <summary>The variable the platform sets to the namespace of the instance's pod.</summary>
    public const string PodNamespaceVariable = "KUBERNETES_POD_NAMESPACE";

    /// <summary>The variable the platform sets to the UID of the instance's pod, which owns its Lease.</summary>
    public const string PodUidVariable = "KUBERNETES_POD_UID";

    /// <summary>The variable the platform sets to the port the instance serves its control plane on.</summary>
    public const string ControlPlanePortVariable = "KUBERNETES_CONTROL_PLANE_PORT";

    /// <summary>The port an instance serves its control plane on unless its environment names another.</summary>
    public const int DefaultControlPlanePort = 9090;

    /// <summary>
    /// Creates the endpoint an instance reaches its gateway at: the namespace whose Leases the gateway
    /// watches, <c>kubernetes:///namespaces/{namespace}</c>.
    /// </summary>
    public static Uri CreateGatewayEndpoint(string ns)
    {
        return new Uri(string.Concat(Scheme, ":///namespaces/", ns));
    }

    /// <summary>
    /// Reads the namespace from a gateway endpoint.
    /// </summary>
    public static bool TryParseGatewayEndpoint(Uri? endpoint, [NotNullWhen(true)] out string? ns)
    {
        ns = null;
        if (endpoint is null || !endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = endpoint.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments[0] != "namespaces" || segments[1].Length == 0)
        {
            return false;
        }

        ns = segments[1];
        return true;
    }

    /// <summary>
    /// Creates the endpoint the gateway reaches an instance's control plane at, through the API server's
    /// pod proxy: <c>kubernetes:///namespaces/{namespace}/pods/{pod}:{port}/</c>.
    /// </summary>
    public static Uri CreateControlPlaneEndpoint(string ns, string pod, int port)
    {
        return new Uri(string.Concat(Scheme, ":///namespaces/", ns, "/pods/", pod, ":", port.ToString(CultureInfo.InvariantCulture), "/"));
    }

    /// <summary>
    /// Reads the namespace, the pod, and the port from a control-plane endpoint.
    /// </summary>
    public static bool TryParseControlPlaneEndpoint(Uri? endpoint, [NotNullWhen(true)] out string? ns, [NotNullWhen(true)] out string? pod, out int port)
    {
        ns = null;
        pod = null;
        port = 0;
        if (endpoint is null || !endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = endpoint.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[0] != "namespaces" || segments[2] != "pods")
        {
            return false;
        }

        int colon = segments[3].LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(segments[3].AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
        {
            return false;
        }

        ns = segments[1];
        pod = segments[3][..colon];
        return ns.Length > 0;
    }
}
