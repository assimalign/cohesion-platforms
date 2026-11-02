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
        /// Runs the gateway's resources on a Kubernetes infrastructure the gateway builds and owns, which
        /// is also the channel their instances publish through and the client the gateway calls their
        /// control planes with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gateway's <see cref="ApplicationGatewayBuilder.Build"/> calls <paramref name="configure"/>
        /// with a new <see cref="KubernetesInfrastructureBuilder"/> and builds the infrastructure from it,
        /// after everything else it checks has passed, so a gateway that is never built connects to no
        /// cluster. What the callback or the infrastructure's creation throws, such as an invalid
        /// namespace or a duplicate image, the gateway's build throws. Disposing the gateway disposes the
        /// infrastructure.
        /// </para>
        /// <para>
        /// Each resource's program opts into the platform too, with <c>UseKubernetes()</c> on its
        /// resource's options from the Kubernetes hosting package; a program that does not fails to
        /// build its resource in its pod.
        /// </para>
        /// </remarks>
        /// <param name="configure">Configures the cluster and binds an image to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseKubernetes(Action<KubernetesInfrastructureBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.UsePlatform(() =>
            {
                var kubernetes = new KubernetesInfrastructureBuilder();
                configure(kubernetes);
                return kubernetes.Build();
            });
        }

        /// <summary>
        /// Runs the gateway's resources on the Kubernetes infrastructure, which is also the channel their
        /// instances publish through and the client the gateway calls their control planes with. The
        /// gateway borrows it: the caller disposes it once the gateway has stopped.
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
