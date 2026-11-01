using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Runs a gateway's resources as containers on a Docker-compatible engine.
/// </summary>
public static class DockerApplicationGatewayBuilderExtensions
{
    extension(ApplicationGatewayBuilder builder)
    {
        /// <summary>
        /// Runs the gateway's resources on the Docker infrastructure, which is also the channel their
        /// instances publish through and the client the gateway calls their control planes with.
        /// </summary>
        /// <remarks>
        /// Each resource's program opts into the platform too, with <c>UseDocker()</c> on its resource's
        /// options from the Docker hosting package; a program that does not fails to build its resource
        /// in its container.
        /// </remarks>
        /// <param name="infrastructure">The infrastructure, with an image bound to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseDocker(DockerInfrastructure infrastructure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(infrastructure);

            return builder.UseInfrastructure(infrastructure).UseInstanceClient(infrastructure);
        }
    }
}
