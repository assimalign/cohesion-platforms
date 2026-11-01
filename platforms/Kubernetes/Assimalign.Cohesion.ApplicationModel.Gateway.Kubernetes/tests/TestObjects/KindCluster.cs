using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// The kind cluster an end-to-end test runs on, and a namespace of its own there that the test deletes
/// when it ends.
/// </summary>
internal sealed class KindCluster : IAsyncDisposable
{
    /// <summary>The variable that names the cluster's kubeconfig context, such as <c>kind-cohesion</c>.</summary>
    public const string ContextVariable = "COHESION_KIND_CONTEXT";

    /// <summary>The variable that names the fixture program's image, when it is not the default.</summary>
    public const string ImageVariable = "COHESION_KIND_WEB_IMAGE";

    /// <summary>The variable that keeps the test's namespace when it ends, to inspect a failed run.</summary>
    public const string KeepNamespaceVariable = "COHESION_KIND_KEEP_NAMESPACE";

    private const string defaultImage = "localhost:5001/cohesion-kubernetes-web:e2e";

    private KindCluster(string context, string kubeConfigPath)
    {
        Context = context;
        KubeConfigPath = kubeConfigPath;
        Namespace = string.Concat("cohesion-e2e-", Guid.NewGuid().ToString("N").AsSpan(0, 8));
        Image = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : defaultImage;
        Client = new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeConfigPath, context));
    }

    public string Context { get; }

    public string KubeConfigPath { get; }

    public string Namespace { get; }

    /// <summary>The fixture program's image, which the cluster pulls from the local registry.</summary>
    public string Image { get; }

    /// <summary>The test's own client of the cluster, apart from the gateway's.</summary>
    public k8s.Kubernetes Client { get; }

    public static KindCluster Connect()
    {
        string context = Environment.GetEnvironmentVariable(ContextVariable)
            ?? throw new InvalidOperationException($"{ContextVariable} is not set.");

        return new KindCluster(context, ResolveKubeConfig());
    }

    /// <summary>
    /// Points the gateway's infrastructure at the cluster and the test's namespace.
    /// </summary>
    public KubernetesInfrastructureBuilder Configure(KubernetesInfrastructureBuilder kubernetes)
    {
        // The fixture image is republished under the same tag, which a node that cached it would not
        // pull again.
        kubernetes.Options.KubeConfigPath = KubeConfigPath;
        kubernetes.Options.Context = Context;
        kubernetes.Options.Namespace = Namespace;
        kubernetes.Options.ImagePullPolicy = "Always";
        return kubernetes;
    }

    /// <summary>
    /// Creates an infrastructure of the test's own over the same namespace, apart from the one the
    /// gateway owns, such as to call each instance's control plane directly.
    /// </summary>
    public KubernetesInfrastructure CreateInfrastructure()
    {
        return Configure(new KubernetesInfrastructureBuilder()).Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(KeepNamespaceVariable) is "1" or "true")
        {
            Client.Dispose();
            return;
        }

        // The gateway leaves what it deployed running, as a platform's workloads outlive their gateway;
        // the test's namespace goes with everything in it.
        try
        {
            await Client.CoreV1.DeleteNamespaceAsync(Namespace, propagationPolicy: "Background", cancellationToken: CancellationToken.None);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The run failed before it created the namespace.
        }

        Client.Dispose();
    }

    // As kubectl resolves it: the first file KUBECONFIG names that exists, then the default kubeconfig.
    private static string ResolveKubeConfig()
    {
        if (Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } variable)
        {
            foreach (string candidate in variable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return KubernetesClientConfiguration.KubeConfigDefaultLocation;
    }
}
