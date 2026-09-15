using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Extensions for selecting the <see cref="KubernetesGateway"/> on an <see cref="IApplicationBuilder"/>.
/// </summary>
public static class KubernetesGatewayExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Selects a <see cref="KubernetesGateway"/> with default options.
        /// </summary>
        /// <returns>The builder, for chaining.</returns>
        public IApplicationBuilder UseKubernetesGateway()
            => builder.UseGateway(new KubernetesGateway());

        /// <summary>
        /// Selects a <see cref="KubernetesGateway"/> configured by <paramref name="configure"/>.
        /// </summary>
        /// <param name="configure">Configures the Kubernetes gateway options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public IApplicationBuilder UseKubernetesGateway(Action<KubernetesGatewayOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var options = new KubernetesGatewayOptions();
            configure(options);
            return builder.UseGateway(new KubernetesGateway(options));
        }

        /// <summary>
        /// Selects Kubernetes and applies common gateway arguments plus the platform-specific
        /// <c>--context</c> and <c>--kubeconfig</c> options.
        /// </summary>
        /// <param name="args">The gateway command-line arguments.</param>
        /// <param name="configure">Optional additional option configuration.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// A common or Kubernetes-specific gateway argument is missing or malformed.
        /// </exception>
        public IApplicationBuilder UseKubernetesGateway(
            string[] args,
            Action<KubernetesGatewayOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            var options = new KubernetesGatewayOptions();
            ApplicationGatewayCommandLine.Apply(options, args);
            KubernetesGatewayCommandLine.Apply(options, args);
            configure?.Invoke(options);
            return builder.UseGateway(new KubernetesGateway(options));
        }
    }

}
