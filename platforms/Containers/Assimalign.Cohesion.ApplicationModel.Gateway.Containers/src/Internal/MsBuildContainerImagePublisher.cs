using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class MsBuildContainerImagePublisher : IContainerImagePublisher
{
    internal static ProcessStartInfo CreateStartInfo(string projectPath, string runtimeIdentifier, string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (runtimeIdentifier is not ("linux-arm64" or "linux-x64"))
        {
            throw new ArgumentException("An image RID must target supported Linux architecture.", nameof(runtimeIdentifier));
        }

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(Path.GetFullPath(projectPath));
        start.ArgumentList.Add("-restore");
        start.ArgumentList.Add("-t:CohesionPublishImages");
        start.ArgumentList.Add("-p:Configuration=Debug");
        start.ArgumentList.Add($"-p:CohesionImageRuntimeIdentifier={runtimeIdentifier}");
        // Compatibility with SDKs predating CohesionImageRuntimeIdentifier.
        start.ArgumentList.Add($"-p:RuntimeIdentifier={runtimeIdentifier}");
        start.ArgumentList.Add($"-p:PublishDir={Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory))}{Path.DirectorySeparatorChar}");
        return start;
    }

    public async Task PublishAsync(string projectPath, string runtimeIdentifier, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine($"Publishing application images: {projectPath} (Debug, {runtimeIdentifier})");
        using Process process = Process.Start(CreateStartInfo(projectPath, runtimeIdentifier, stagingDirectory))
            ?? throw new InvalidOperationException("Could not start the SDK image publisher.");
        Task output = RelayAsync(process.StandardOutput, Console.Out);
        Task error = RelayAsync(process.StandardError, Console.Error);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"CohesionPublishImages failed for '{projectPath}' (exit {process.ExitCode}).");
        }
    }

    private static async Task RelayAsync(StreamReader source, TextWriter destination)
    {
        while (await source.ReadLineAsync().ConfigureAwait(false) is string line)
        {
            await destination.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}
