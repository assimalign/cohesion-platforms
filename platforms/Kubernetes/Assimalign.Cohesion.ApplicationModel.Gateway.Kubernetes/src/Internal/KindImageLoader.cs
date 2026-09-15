using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal interface IKindImageLoader
{
    Task<KindImageLoadResult> LoadIfKindAsync(
        string archivePath,
        string digest,
        CancellationToken cancellationToken);
}

internal enum KindImageLoadResult
{
    NotKind,
    Loaded,
    KindUnavailable,
}

internal interface IKindCommandRunner
{
    Task<KindCommandResult?> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface IKubernetesContextResolver
{
    string? ResolveCurrentContext(KubernetesGatewayOptions options);
}

internal sealed record KindCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed class KubernetesContextResolver : IKubernetesContextResolver
{
    public string? ResolveCurrentContext(KubernetesGatewayOptions options) =>
        KubernetesClientFactory.ResolveConfiguration(options).CurrentContext;
}

internal sealed class KindImageLoader : IKindImageLoader
{
    private const string contextPrefix = "kind-";

    private readonly KubernetesGatewayOptions _options;
    private readonly IKubernetesContextResolver _contexts;
    private readonly IKindCommandRunner _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _loadedDigests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingExecutableContexts = new(StringComparer.Ordinal);

    public KindImageLoader(
        KubernetesGatewayOptions options,
        IKubernetesContextResolver contexts,
        IKindCommandRunner commands)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(commands);
        _options = options;
        _contexts = contexts;
        _commands = commands;
    }

    public async Task<KindImageLoadResult> LoadIfKindAsync(
        string archivePath,
        string digest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        cancellationToken.ThrowIfCancellationRequested();

        string? context = _contexts.ResolveCurrentContext(_options);
        if (!TryGetClusterName(context, out string clusterName))
        {
            return KindImageLoadResult.NotKind;
        }

        string digestKey = $"{context}\n{digest}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedDigests.Contains(digestKey))
            {
                return KindImageLoadResult.Loaded;
            }

            if (_missingExecutableContexts.Contains(context!))
            {
                return KindImageLoadResult.KindUnavailable;
            }

            KindCommandResult? result = await _commands
                .RunAsync(
                    ["load", "image-archive", archivePath, "--name", clusterName],
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                _missingExecutableContexts.Add(context!);
                _options.WarningHandler(
                    $"Kubernetes context '{context}' is a Kind context, but the 'kind' executable " +
                    $"was not found. Image archive '{archivePath}' was not loaded.");
                return KindImageLoadResult.KindUnavailable;
            }

            if (result.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                string suffix = string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : $" {detail.Trim()}";
                throw new InvalidOperationException(
                    $"kind load image-archive failed for cluster '{clusterName}', archive " +
                    $"'{archivePath}', and digest '{digest}' with exit code {result.ExitCode}.{suffix}");
            }

            _loadedDigests.Add(digestKey);
            return KindImageLoadResult.Loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryGetClusterName(string? context, out string clusterName)
    {
        if (context is not null
            && context.StartsWith(contextPrefix, StringComparison.Ordinal)
            && context.Length > contextPrefix.Length)
        {
            clusterName = context[contextPrefix.Length..];
            return true;
        }

        clusterName = string.Empty;
        return false;
    }
}

internal sealed class KindCommandRunner : IKindCommandRunner
{
    public async Task<KindCommandResult?> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = "kind",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        for (int index = 0; index < arguments.Count; index++)
        {
            startInfo.ArgumentList.Add(arguments[index]);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (process is null)
        {
            throw new InvalidOperationException("The 'kind' process could not be started.");
        }

        using (process)
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            return new KindCommandResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
