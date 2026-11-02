using System;

using Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes;

/// <summary>
/// Opts a resource of any area into the Kubernetes platform.
/// </summary>
public static class KubernetesApplicationInstanceOptionsExtensions
{
    extension<TOptions>(TOptions options) where TOptions : ApplicationInstanceOptions
    {
        /// <summary>
        /// Opts the resource into the Kubernetes platform: when a gateway runs the instance on
        /// Kubernetes, the instance publishes to it and serves its control plane through the
        /// cluster's API server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client reads what the gateway's platform gave the pod now, through
        /// <see cref="AppEnvironment"/>: the namespace its gateway watches
        /// (<see cref="AppEnvironment.Variables.ManifestEndpoint"/>), and the pod's own name, namespace,
        /// UID, and control-plane port. Call it while the resource's options are configured.
        /// </para>
        /// <para>
        /// On any other platform the client is not used: a resource that runs on Kubernetes and
        /// elsewhere opts into each platform, and an instance uses the client of the one its gateway
        /// names.
        /// </para>
        /// </remarks>
        /// <returns>The same options, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The resource already uses a Kubernetes client.</exception>
        public TOptions UseKubernetes()
        {
            ArgumentNullException.ThrowIfNull(options);

            options.UseGatewayClient(new KubernetesGatewayClient(KubernetesInstanceEnvironment.Read()));
            return options;
        }
    }
}
