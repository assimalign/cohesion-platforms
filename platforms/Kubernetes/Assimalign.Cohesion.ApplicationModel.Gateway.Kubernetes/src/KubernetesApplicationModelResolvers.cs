using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Creates application-model resolvers that use a Kubernetes operator identity.
/// </summary>
public static class KubernetesApplicationModelResolvers
{
    /// <summary>
    /// Imports the secret-free <c>cohesion-export</c> ConfigMap from the selected kubeconfig
    /// context. Kubeconfig access is an operator trust channel and is not application trust.
    /// </summary>
    /// <param name="application">The application namespace and expected export identity.</param>
    /// <param name="options">The Kubernetes connection options.</param>
    /// <returns>A resolver suitable for an <see cref="IApplicationSet"/> member.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A Kubernetes connection option is invalid.</exception>
    public static IApplicationModelResolver ImportFromKubernetes(
        ApplicationName application,
        KubernetesGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateKubernetes();
        return new KubernetesApplicationModelResolver(application, options);
    }
}
