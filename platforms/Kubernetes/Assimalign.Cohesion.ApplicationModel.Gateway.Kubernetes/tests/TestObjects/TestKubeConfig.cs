using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// A kubeconfig for a cluster nothing listens at, for tests that never reach a cluster.
/// </summary>
internal sealed class TestKubeConfig : IDisposable
{
    public TestKubeConfig()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cohesion-kubeconfig-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(Path, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: test
              cluster:
                server: https://127.0.0.1:1
                insecure-skip-tls-verify: true
            contexts:
            - name: test
              context:
                cluster: test
                user: test
            current-context: test
            users:
            - name: test
              user:
                token: test-token
            """);
    }

    public string Path { get; }

    public KubernetesInfrastructure Create(string ns = "cohesion")
    {
        return KubernetesInfrastructure.Create(new KubernetesInfrastructureOptions { KubeConfigPath = Path, Namespace = ns });
    }

    public void Dispose()
    {
        File.Delete(Path);
    }
}
