using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>Applies Kubernetes switches before the SDK configures its common control plane.</summary>
// Deviates from the repo interface-first rule per the approved SDK public static hook contract.
public static class KubernetesGatewayCommandLine
{
    /// <summary>Applies platform arguments; unknown switches are ignored.</summary>
    /// <param name="options">The options to update.</param>
    /// <param name="args">The gateway arguments.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A recognized switch is empty, missing, or invalid.</exception>
    public static void Apply(KubernetesGatewayOptions options, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);
        // Private gateway-installation inputs; these are never workload runtime or credential keys.
        options.FieldManager = Environment.GetEnvironmentVariable(KubernetesSystemInstallation.FieldManagerVariable) ?? options.FieldManager;
        options.ExportDirectory = Environment.GetEnvironmentVariable(KubernetesSystemInstallation.StateDirectoryVariable) ?? options.ExportDirectory;
        options.SystemIngressClass = Environment.GetEnvironmentVariable(KubernetesSystemInstallation.IngressClassVariable) ?? options.SystemIngressClass;
        ApplyKubernetesArguments(options, args);
        // The SDK invokes this hook before GatewayControlPlane.Configure captures the path.
        // On developer hosts, leaving it null preserves the upstream .cohesion default.
    }

    private static void ApplyKubernetesArguments(KubernetesGatewayOptions options, string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (TryReadValue(argument, "--context", out string? context))
            {
                options.ContextName = context ?? RequireFollowing(args, ref index, "--context");
            }
            else if (TryReadValue(argument, "--kubeconfig", out string? kubeconfig))
            {
                options.KubeConfigPath = kubeconfig ?? RequireFollowing(args, ref index, "--kubeconfig");
            }
            else if (TryReadValue(argument, "--cohesion-system-namespace", out string? systemNamespace))
            {
                options.SystemNamespace = systemNamespace ?? RequireFollowing(args, ref index, "--cohesion-system-namespace");
            }
            else if (TryReadValue(argument, "--cohesion-system-image", out string? image))
            {
                options.SystemImage = image ?? RequireFollowing(args, ref index, "--cohesion-system-image");
            }
            else if (TryReadValue(argument, "--cohesion-system-service-account", out string? account))
            {
                options.SystemServiceAccount = account ?? RequireFollowing(args, ref index, "--cohesion-system-service-account");
            }
            else if (TryReadValue(argument, "--cohesion-system-storage", out string? storage))
            {
                options.SystemStorageSize = storage ?? RequireFollowing(args, ref index, "--cohesion-system-storage");
            }
            else if (TryReadValue(argument, "--control-plane-host", out string? host))
            {
                options.SystemIngressHost = host ?? RequireFollowing(args, ref index, "--control-plane-host");
            }
            else if (TryReadValue(argument, "--control-plane-expose", out string? exposure))
            {
                string value = exposure ?? RequireFollowing(args, ref index, "--control-plane-expose");
                options.SystemExposure = value.ToLowerInvariant() switch
                {
                    "none" => KubernetesSystemExposure.None,
                    "loadbalancer" => KubernetesSystemExposure.LoadBalancer,
                    "ingress" => KubernetesSystemExposure.Ingress,
                    _ => throw new ArgumentException("Gateway argument '--control-plane-expose' requires none, loadbalancer, or ingress.", nameof(args)),
                };
            }
            else if (TryReadValue(argument, "--bootstrap-apply", out string? bootstrap))
            {
                if (bootstrap is null && index + 1 < args.Length && bool.TryParse(args[index + 1], out _))
                {
                    bootstrap = args[++index];
                }

                if (!bool.TryParse(bootstrap ?? "true", out bool apply))
                {
                    throw new ArgumentException("Gateway argument '--bootstrap-apply' requires true or false.", nameof(args));
                }

                options.BootstrapApply = apply;
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
        if (++index >= args.Length
            || string.IsNullOrWhiteSpace(args[index])
            || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Gateway argument '{name}' requires a value.", nameof(args));
        }

        return args[index];
    }
}
