namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Configures the cluster a <see cref="KubernetesInfrastructure"/> deploys to and the shape of what it
/// deploys there.
/// </summary>
public sealed class KubernetesInfrastructureOptions
{
    /// <summary>
    /// Gets or sets the kubeconfig file; <see langword="null"/> resolves the cluster as kubectl does:
    /// the <c>KUBECONFIG</c> variable, the default kubeconfig, then the pod's service account when the
    /// gateway runs in the cluster.
    /// </summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>
    /// Gets or sets the kubeconfig context, such as <c>kind-cohesion</c>; <see langword="null"/> uses
    /// the current one.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Gets or sets the namespace the gateway runs its resources in; <c>cohesion</c> unless set.
    /// </summary>
    /// <remarks>
    /// A namespace belongs to one gateway: the gateway watches every instance Lease in it.
    /// </remarks>
    public string Namespace { get; set; } = "cohesion";

    /// <summary>
    /// Gets or sets the port every resource's <c>http</c> endpoint listens on inside its pod; 8080
    /// unless set. Its Service exposes it on port 80.
    /// </summary>
    public int EndpointPort { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the port every instance serves its control plane on inside its pod; 9090 unless
    /// set. Only the cluster network and the API server's pod proxy reach it.
    /// </summary>
    public int ControlPlanePort { get; set; } = 9090;

    /// <summary>
    /// Gets or sets the image pull policy of every resource's container; <c>IfNotPresent</c> unless
    /// set.
    /// </summary>
    public string ImagePullPolicy { get; set; } = "IfNotPresent";
}
