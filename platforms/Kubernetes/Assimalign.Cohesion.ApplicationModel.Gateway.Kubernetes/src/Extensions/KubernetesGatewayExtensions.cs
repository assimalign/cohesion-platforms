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
    }
}
