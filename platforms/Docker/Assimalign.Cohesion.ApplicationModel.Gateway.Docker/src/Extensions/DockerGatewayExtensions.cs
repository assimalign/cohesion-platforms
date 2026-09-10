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
        /// Selects Docker and applies common gateway arguments plus <c>--docker-host</c> and
        /// repeatable <c>--image-archive=&lt;digest-reference&gt;=&lt;path&gt;</c> arguments.
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
            ApplyDockerArguments(options, args);
            configure?.Invoke(options);
            return builder.UseGateway(new DockerGateway(options));
        }
    }

    private static void ApplyDockerArguments(DockerGatewayOptions options, string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (TryReadValue(argument, "--docker-host", out string? endpoint))
            {
                string value = endpoint ?? RequireFollowing(args, ref index, "--docker-host");
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                {
                    throw new ArgumentException(
                        $"Gateway argument '--docker-host' value '{value}' is not an absolute URI.",
                        nameof(args));
                }

                options.EngineEndpoint = uri;
            }
            else if (TryReadValue(argument, "--image-archive", out string? mapping))
            {
                string value = mapping ?? RequireFollowing(args, ref index, "--image-archive");
                int separator = value.LastIndexOf('=');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new ArgumentException(
                        "Gateway argument '--image-archive' requires <digest-reference>=<path>.",
                        nameof(args));
                }

                options.ImageArchives[value[..separator]] = value[(separator + 1)..];
            }
        }
    }

    private static bool TryReadValue(string argument, string name, out string? value)
    {
        value = null;
        if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = name + "=";
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = argument[prefix.Length..];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"Gateway argument '{name}' requires a value.",
                nameof(argument));
        }

        return true;
    }

    private static string RequireFollowing(string[] args, ref int index, string name)
    {
        if (++index >= args.Length
            || string.IsNullOrWhiteSpace(args[index])
            || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Gateway argument '{name}' requires a value.",
                nameof(args));
        }

        return args[index];
    }
}
