using System;
using System.Net;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>Applies Docker-specific arguments to options before gateway configuration.</summary>
// Deviates from the repo interface-first rule per item 37: generated provider code requires a public static entry point.
public static class DockerGatewayCommandLine
{
    /// <summary>
    /// Applies <c>--docker-host</c>, repeatable <c>--image-archive</c>, and
    /// <c>--control-plane-bind</c> arguments. Unknown arguments are ignored.
    /// </summary>
    /// <param name="options">The options to update.</param>
    /// <param name="args">Arguments in separated or equals-value form.</param>
    /// <exception cref="ArgumentNullException">Options or arguments are null.</exception>
    /// <exception cref="ArgumentException">A recognized argument has a missing or invalid value.</exception>
    public static void Apply(DockerGatewayOptions options, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);
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
                int separator = value.IndexOf('=');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new ArgumentException(
                        "Gateway argument '--image-archive' requires <digest-reference>=<path>.",
                        nameof(args));
                }

                options.ImageArchives[value[..separator]] = value[(separator + 1)..];
            }
            else if (TryReadValue(argument, "--control-plane-bind", out string? binding))
            {
                string value = binding ?? RequireFollowing(args, ref index, "--control-plane-bind");
                string address = value.Contains("://", StringComparison.Ordinal)
                    ? value
                    : IPAddress.TryParse(value.Trim('[', ']'), out IPAddress? ip)
                        ? new UriBuilder(Uri.UriSchemeHttp, ip.ToString(), 0).Uri.AbsoluteUri
                        : value.Contains(':', StringComparison.Ordinal)
                            ? "http://" + value
                            : "http://" + value + ":0";
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
                {
                    throw new ArgumentException("Gateway argument '--control-plane-bind' is not an HTTP bind address.", nameof(args));
                }

                DockerGatewayOptions.ValidateControlPlaneAddress(uri);
                options.ControlPlaneAddress = uri;
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
            throw new ArgumentException($"Gateway argument '{name}' requires a value.", nameof(argument));
        }

        return true;
    }

    private static string RequireFollowing(string[] args, ref int index, string name)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
            || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Gateway argument '{name}' requires a value.", nameof(args));
        }

        return args[index];
    }
}
