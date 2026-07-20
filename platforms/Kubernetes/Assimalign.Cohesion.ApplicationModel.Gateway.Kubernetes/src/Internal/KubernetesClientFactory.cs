using System;
using System.IO;

using k8s;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Resolves a <see cref="IKubernetes"/> client from the gateway options, mirroring the
/// conventional kubectl resolution order: explicit path, <c>KUBECONFIG</c>, default
/// kubeconfig file, then in-cluster service account.
/// </summary>
internal static class KubernetesClientFactory
{
    /// <summary>
    /// Creates a client for the cluster the options resolve to.
    /// </summary>
    /// <param name="options">The gateway options carrying the kubeconfig hints.</param>
    /// <returns>A connected <see cref="IKubernetes"/> client. The caller owns disposal.</returns>
    /// <exception cref="FileNotFoundException">
    /// <see cref="KubernetesGatewayOptions.KubeConfigPath"/> is set but the file does not exist.
    /// </exception>
    /// <exception cref="InvalidOperationException">No cluster configuration could be resolved.</exception>
    public static IKubernetes Create(KubernetesGatewayOptions options)
    {
        return new k8s.Kubernetes(ResolveConfiguration(options));
    }

    private static KubernetesClientConfiguration ResolveConfiguration(KubernetesGatewayOptions options)
    {
        if (options.KubeConfigPath is not null)
        {
            if (!File.Exists(options.KubeConfigPath))
            {
                throw new FileNotFoundException(
                    $"The kubeconfig file '{options.KubeConfigPath}' set on {nameof(KubernetesGatewayOptions)}.{nameof(KubernetesGatewayOptions.KubeConfigPath)} does not exist.",
                    options.KubeConfigPath);
            }

            return KubernetesClientConfiguration.BuildConfigFromConfigFile(options.KubeConfigPath, options.ContextName);
        }

        // KUBECONFIG may carry a path list; the first existing file wins.
        string? environmentValue = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            foreach (string candidate in environmentValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(candidate))
                {
                    return KubernetesClientConfiguration.BuildConfigFromConfigFile(candidate, options.ContextName);
                }
            }
        }

        if (File.Exists(KubernetesClientConfiguration.KubeConfigDefaultLocation))
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(
                KubernetesClientConfiguration.KubeConfigDefaultLocation, options.ContextName);
        }

        if (KubernetesClientConfiguration.IsInCluster())
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }

        throw new InvalidOperationException(
            "No Kubernetes cluster configuration could be resolved. Set " +
            $"{nameof(KubernetesGatewayOptions)}.{nameof(KubernetesGatewayOptions.KubeConfigPath)}, set the KUBECONFIG " +
            $"environment variable, place a kubeconfig at '{KubernetesClientConfiguration.KubeConfigDefaultLocation}', " +
            "or run inside a cluster with a service account mounted.");
    }
}
