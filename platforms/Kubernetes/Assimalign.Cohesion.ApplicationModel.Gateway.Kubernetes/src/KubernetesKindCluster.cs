using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>Provisions the Podman-backed Kind cluster and its local registry route.</summary>
public static class KubernetesKindCluster
{
    /// <summary>Runs the bundled provisioning script; recreation requires an explicit request.</summary>
    /// <param name="name">The Kind cluster name; its context is kind-&lt;name&gt;.</param>
    /// <param name="recreate">Whether to explicitly replace an existing cluster.</param>
    /// <param name="cancellationToken">Cancels the provisioning process tree.</param>
    /// <returns>A task completing after cluster and registry verification.</returns>
    /// <exception cref="ArgumentException">The cluster name is not a DNS label.</exception>
    /// <exception cref="InvalidOperationException">Provisioning could not start or failed.</exception>
    /// <exception cref="IOException">The bundled script could not be extracted.</exception>
    /// <exception cref="OperationCanceledException">Provisioning was canceled.</exception>
    public static async Task ProvisionAsync(string name = "cohesion", bool recreate = false,
        CancellationToken cancellationToken = default)
    {
        KubernetesMetadata.RequireDnsLabel(name, nameof(name));
        string script = Path.Combine(Path.GetTempPath(), $"cohesion-kind-{Guid.NewGuid():N}.ps1");
        try
        {
            using Stream source = typeof(KubernetesKindCluster).Assembly.GetManifestResourceStream("cohesion/New-CohesionKindCluster.ps1")!;
            await using (FileStream destination = File.Create(script))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-Name");
            start.ArgumentList.Add(name);
            if (recreate) { start.ArgumentList.Add("-Recreate"); }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Kind provisioning.");
            Task output = RelayAsync(process.StandardOutput, Console.Out);
            Task error = RelayAsync(process.StandardError, Console.Error);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                throw;
            }
            if (process.ExitCode != 0) { throw new InvalidOperationException($"Kind provisioning failed (exit {process.ExitCode})."); }
        }
        finally
        {
            File.Delete(script);
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
