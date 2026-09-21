using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Extensions for selecting <see cref="DockerGateway"/> on an <see cref="IApplicationBuilder"/>.
/// </summary>
public static class DockerGatewayExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Selects a Docker gateway with default options.</summary>
        /// <returns>The builder, for chaining.</returns>
        public IApplicationBuilder UseDockerGateway() =>
            builder.UseGateway(new DockerGateway());

        /// <summary>Selects a Docker gateway configured by <paramref name="configure"/>.</summary>
        /// <param name="configure">Configures Docker connection and supervision options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured Docker options are invalid.</exception>
        /// <exception cref="NotSupportedException">The configured Engine endpoint scheme is unsupported.</exception>
        public IApplicationBuilder UseDockerGateway(Action<DockerGatewayOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new DockerGatewayOptions();
            configure(options);
            return builder.UseGateway(new DockerGateway(options));
        }

        /// <summary>
        /// Selects Docker and applies common gateway arguments plus <c>--docker-host</c>,
        /// <c>--control-plane-bind</c>, and repeatable
        /// <c>--image-archive=&lt;digest-reference&gt;=&lt;path&gt;</c> arguments before configuration.
        /// </summary>
        /// <param name="args">The gateway command-line arguments.</param>
        /// <param name="configure">Optional additional Docker option configuration.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">A Docker gateway argument is malformed.</exception>
        /// <exception cref="NotSupportedException">The selected Engine endpoint scheme is unsupported.</exception>
        public IApplicationBuilder UseDockerGateway(
            string[] args,
            Action<DockerGatewayOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            var options = new DockerGatewayOptions();
            ApplicationGatewayCommandLine.Apply(options, args);
            DockerGatewayCommandLine.Apply(options, args);
            configure?.Invoke(options);
            return builder.UseGateway(new DockerGateway(options));
        }
    }

}
