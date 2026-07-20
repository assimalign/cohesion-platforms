using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Options controlling how the <see cref="KubernetesGateway"/> connects to a cluster,
/// applies objects, and bounds readiness and teardown.
/// </summary>
public sealed class KubernetesGatewayOptions
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
    /// The maximum time to wait for a resource to become ready before treating startup as
    /// failed. Defaults to 60&#160;seconds.
    /// </summary>
    public TimeSpan ReadinessBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The budget for deleting the application namespace when the gateway stops. Deletion
    /// still in progress when the budget elapses is abandoned best-effort. Defaults to
    /// 30&#160;seconds.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);
}
