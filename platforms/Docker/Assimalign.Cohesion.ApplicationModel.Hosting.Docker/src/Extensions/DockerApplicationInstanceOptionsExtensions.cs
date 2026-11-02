using System;

using Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker;

/// <summary>
/// Opts a resource of any area into the Docker platform.
/// </summary>
public static class DockerApplicationInstanceOptionsExtensions
{
    extension<TOptions>(TOptions options) where TOptions : ApplicationInstanceOptions
    {
        /// <summary>
        /// Opts the resource into the Docker platform: when a gateway runs the instance as a container
        /// on a Docker-compatible engine, the instance publishes to it and serves its control plane on
        /// its container's control-plane port, where the gateway polls and calls it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client reads what the gateway's platform gave the container now, through
        /// <see cref="AppEnvironment"/>: the network its gateway runs
        /// (<see cref="AppEnvironment.Variables.ManifestEndpoint"/>), the container's own name, and its
        /// control-plane port. Call it while the resource's options are configured.
        /// </para>
        /// <para>
        /// On any other platform the client is not used: a resource that runs on Docker and elsewhere
        /// opts into each platform, and an instance uses the client of the one its gateway names.
        /// </para>
        /// </remarks>
        /// <returns>The same options, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The resource already uses a Docker client.</exception>
        public TOptions UseDocker()
        {
            ArgumentNullException.ThrowIfNull(options);

            options.UseGatewayClient(new DockerGatewayClient(DockerInstanceEnvironment.Read()));
            return options;
        }
    }
}
