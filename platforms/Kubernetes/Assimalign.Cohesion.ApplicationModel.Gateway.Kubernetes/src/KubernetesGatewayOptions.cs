using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Options controlling how the <see cref="KubernetesGateway"/> connects to a cluster,
/// registers controller overrides, applies objects, and bounds readiness and teardown.
/// </summary>
public sealed class KubernetesGatewayOptions : ApplicationGatewayOptions
{
    /// <summary>
    /// An explicit kubeconfig file path. When <see langword="null"/>, the gateway resolves
    /// a configuration from the <c>KUBECONFIG</c> environment variable, then the default
    /// kubeconfig file (<c>~/.kube/config</c>), then the in-cluster service account.
    /// </summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>
    /// The kubeconfig context to use, overriding the file's <c>current-context</c>.
    /// Ignored when the configuration is resolved in-cluster. When <see langword="null"/>,
    /// the kubeconfig's own current context is used.
    /// </summary>
    public string? ContextName { get; set; }

    /// <summary>
    /// The server-side-apply field manager under which the gateway claims ownership of the
    /// fields it applies. Defaults to <c>cohesion-gateway</c>.
    /// </summary>
    public string FieldManager { get; set; } = "cohesion-gateway";

    /// <summary>
    /// The legacy scaffold budget for namespace cleanup during observer shutdown. The plan
    /// controller moves namespace deletion to uninstall/teardown. Defaults to 30&#160;seconds.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);
}
