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
        /// Runs the gateway's resources on a Docker infrastructure the gateway builds and owns, which is
        /// also the channel their instances publish through and the client the gateway calls their
        /// control planes with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gateway's <see cref="ApplicationGatewayBuilder.Build"/> calls <paramref name="configure"/>
        /// with a new <see cref="DockerInfrastructureBuilder"/> and builds the infrastructure from it,
        /// after everything else it checks has passed, so a gateway that is never built creates no
        /// infrastructure. What the callback or the infrastructure's creation throws, such as an invalid
        /// network or a duplicate image, the gateway's build throws. Disposing the gateway disposes the
        /// infrastructure.
        /// </para>
        /// <para>
        /// Each resource's program opts into the platform too, with <c>UseDocker()</c> on its resource's
        /// options from the Docker hosting package; a program that does not fails to build its resource
        /// in its container.
        /// </para>
        /// </remarks>
        /// <param name="configure">Configures the engine and binds an image to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseDocker(Action<DockerInfrastructureBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.UsePlatform(() =>
            {
                var docker = new DockerInfrastructureBuilder();
                configure(docker);
                return docker.Build();
            });
        }

        /// <summary>
        /// Runs the gateway's resources on the Docker infrastructure, which is also the channel their
        /// instances publish through and the client the gateway calls their control planes with. The
        /// gateway borrows it: the caller disposes it once the gateway has stopped.
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
