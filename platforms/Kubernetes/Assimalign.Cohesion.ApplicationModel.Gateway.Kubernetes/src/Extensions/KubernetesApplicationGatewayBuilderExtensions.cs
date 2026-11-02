using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Runs a gateway's resources on a Kubernetes cluster.
/// </summary>
public static class KubernetesApplicationGatewayBuilderExtensions
{
    extension(ApplicationGatewayBuilder builder)
    {
        /// <summary>
        /// Runs the gateway's resources on the Kubernetes infrastructure, which is also the channel their
        /// instances publish through and the client the gateway calls their control planes with.
        /// </summary>
        /// <remarks>
        /// Each resource's program opts into the platform too, with <c>UseKubernetes()</c> on its
        /// resource's options from the Kubernetes hosting package; a program that does not fails to
        /// build its resource in its pod.
        /// </remarks>
        /// <param name="infrastructure">The infrastructure, with an image bound to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseKubernetes(KubernetesInfrastructure infrastructure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(infrastructure);

            return builder.UseInfrastructure(infrastructure).UseInstanceClient(infrastructure);
        }
    }
}
