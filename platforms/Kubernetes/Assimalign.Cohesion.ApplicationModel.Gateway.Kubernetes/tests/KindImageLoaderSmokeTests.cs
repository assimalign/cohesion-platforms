using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KindImageLoaderSmokeTests
{
    [KindSmokeFact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader smoke: Should load an opted-in OCI archive")]
    public async Task LoadIfKindAsync_OnOptedInCluster_ShouldLoadArchive()
    {
        // Arrange
        string archivePath = RequireEnvironment("COHESION_KIND_SMOKE_ARCHIVE");
        string digest = RequireEnvironment("COHESION_KIND_SMOKE_DIGEST");
        _ = ContainerImageArtifacts.Create(
            default,
            $"cohesion-smoke@{digest}");
        string clusterName = RequireEnvironment("COHESION_KIND_SMOKE_CLUSTER");
        var commands = new KindCommandRunner();

        KindCommandResult? version = await commands.RunAsync(
            ["version"],
            CancellationToken.None);
        if (version is null)
        {
            throw new InvalidOperationException("The opted-in 'kind' executable disappeared from PATH.");
        }

        KindCommandResult? clusters = await commands.RunAsync(
            ["get", "clusters"],
            CancellationToken.None);
        if (clusters is null || clusters.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Kind could not enumerate clusters for the selected provider.");
        }

        bool clusterExists = clusters.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(clusterName, StringComparer.Ordinal);
        if (!clusterExists)
        {
            throw new InvalidOperationException(
                $"Kind cluster '{clusterName}' does not exist for the selected provider.");
        }

        var loader = new KindImageLoader(
            new KubernetesGatewayOptions(),
            new FixedContextResolver($"kind-{clusterName}"),
            commands);

        // Act
        await loader.LoadIfKindAsync(archivePath, digest, CancellationToken.None);

        // Assert: LoadIfKindAsync returning is the Kind image-load compatibility assertion.
    }

    private static string RequireEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The opted-in Kind smoke test requires environment variable {name}.");
        }

        return value;
    }

    private sealed class FixedContextResolver : IKubernetesContextResolver
    {
        private readonly string _context;

        public FixedContextResolver(string context) => _context = context;

        public string ResolveCurrentContext(KubernetesGatewayOptions options) => _context;
    }
}

internal sealed class KindSmokeFactAttribute : FactAttribute
{
    public KindSmokeFactAttribute()
    {
        Skip = GetSkipReason();
    }

    private static string? GetSkipReason()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("COHESION_RUN_KIND_SMOKE"),
            "1",
            StringComparison.Ordinal))
        {
            return "Set COHESION_RUN_KIND_SMOKE=1 to run the opt-in Kind image-archive smoke test.";
        }

        string? archivePath = Environment.GetEnvironmentVariable("COHESION_KIND_SMOKE_ARCHIVE");
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return "Set COHESION_KIND_SMOKE_ARCHIVE to an existing OCI archive.";
        }

        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("COHESION_KIND_SMOKE_DIGEST")))
        {
            return "Set COHESION_KIND_SMOKE_DIGEST to the archive's sha256 manifest digest.";
        }

        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("COHESION_KIND_SMOKE_CLUSTER")))
        {
            return "Set COHESION_KIND_SMOKE_CLUSTER to an existing Kind cluster name.";
        }

        return IsKindOnPath() ? null : "The 'kind' executable is not available on PATH.";
    }

    private static bool IsKindOnPath()
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        string[] executableNames = OperatingSystem.IsWindows()
            ? ["kind.exe", "kind"]
            : ["kind"];
        foreach (string directoryValue in pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = directoryValue.Trim('"');
            for (int index = 0; index < executableNames.Length; index++)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, executableNames[index])))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return false;
    }
}
