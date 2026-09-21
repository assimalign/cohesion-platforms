using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>Shared Local image publication and index reload for container gateways.</summary>
public static class ContainerImagePublishing
{
    /// <summary>Maps a Linux target's architecture to the SDK image RID.</summary>
    /// <param name="architecture">The target engine or node architecture.</param>
    /// <returns>The corresponding Linux runtime identifier.</returns>
    /// <exception cref="ArgumentNullException">The architecture is null.</exception>
    /// <exception cref="NotSupportedException">The target architecture is unsupported.</exception>
    public static string RuntimeIdentifier(string architecture) => (architecture ?? throw new ArgumentNullException(nameof(architecture))).ToLowerInvariant() switch
    {
        "arm64" or "aarch64" => "linux-arm64",
        "amd64" or "x86_64" or "x64" => "linux-x64",
        _ => throw new NotSupportedException($"Container target architecture '{architecture}' is not supported."),
    };

    /// <summary>Creates the SDK target invoker.</summary>
    /// <returns>An invoker using dotnet msbuild and argument-list process execution.</returns>
    public static IContainerImagePublisher CreatePublisher() => new MsBuildContainerImagePublisher();

    /// <summary>
    /// Uses an explicit index unchanged, or publishes a Local source application and reloads its
    /// index. The architecture callback is never invoked for an explicit index or a package model.
    /// </summary>
    /// <param name="model">The source application's validated model.</param>
    /// <param name="imageIndexPath">An explicit pre-published index, which bypasses publishing.</param>
    /// <param name="targetArchitecture">Reads the target's architecture when publishing is needed.</param>
    /// <param name="publisher">The SDK target invocation seam.</param>
    /// <param name="cancellationToken">Cancels target inspection, publication, or index reading.</param>
    /// <returns>The validated index path, or null for a model using package image identities.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The source apphost cannot be identified or publication failed.</exception>
    /// <exception cref="InvalidDataException">The index is malformed or belongs to another application.</exception>
    /// <exception cref="IOException">An index or publication file cannot be accessed.</exception>
    /// <exception cref="NotSupportedException">The target architecture is unsupported.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<string?> PrepareAsync(IApplicationModel model, string? imageIndexPath,
        Func<CancellationToken, Task<string>> targetArchitecture, IContainerImagePublisher publisher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(targetArchitecture);
        ArgumentNullException.ThrowIfNull(publisher);
        if (imageIndexPath is not null)
        {
            await ReloadAsync(imageIndexPath, model.Name, cancellationToken).ConfigureAwait(false);
            return imageIndexPath;
        }
        if (!model.Environment.IsLocal || !HasSource(model))
        {
            return null;
        }

        string project = FindProject(model);
        string rid = RuntimeIdentifier(await targetArchitecture(cancellationToken).ConfigureAwait(false));
        string staging = Path.Combine(Path.GetDirectoryName(project)!, "obj", "cohesion", "images", "Debug", rid);
        Directory.CreateDirectory(staging);
        await publisher.PublishAsync(project, rid, staging, cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(staging, "application.images.json");
        await ReloadAsync(path, model.Name, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Reads the newly produced index and verifies its application identity.</summary>
    /// <param name="path">The application image index path.</param>
    /// <param name="application">The expected application identity.</param>
    /// <param name="cancellationToken">Cancels index reading.</param>
    /// <returns>The validated, freshly read index.</returns>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="InvalidDataException">The document or application identity is invalid.</exception>
    /// <exception cref="IOException">The index cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<IApplicationImageIndex> ReloadAsync(string path, ApplicationName application,
        CancellationToken cancellationToken = default)
    {
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(path, cancellationToken).ConfigureAwait(false);
        if (index.Application != application)
        {
            throw new InvalidDataException($"Image index '{path}' belongs to application '{index.Application}', not application '{application}'.");
        }
        return index;
    }

    private static bool HasSource(IApplicationModel model)
    {
        foreach (ResourceManifest manifest in model.Manifests)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Artifact.Project))
            {
                return true;
            }
        }
        return false;
    }

    internal static string FindProject(IApplicationModel model)
    {
        string? project = null;
        foreach (ResourceManifest manifest in model.Manifests)
        {
            if (manifest.Application == model.Name.Value && manifest.ApplicationModel ==
                "Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane" && manifest.Artifact.Project is { Length: > 0 } candidate)
            {
                if (project is not null && project != candidate)
                {
                    throw new InvalidOperationException($"Application '{model.Name}' contains multiple apphost projects.");
                }

                project = candidate;
            }
        }
        // Current models contain only member manifests. Read the running apphost's own SDK
        // manifest without widening IApplicationModel or reflecting over generated CLR members.
        if (project is null)
        {
            using Stream? stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream("cohesion/resource.json");
            if (stream is not null)
            {
                ResourceManifest manifest = ResourceManifest.Load(stream);
                if (manifest.Application == model.Name.Value && manifest.ApplicationModel ==
                    "Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane")
                {
                    project = manifest.Artifact.Project;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException($"Application '{model.Name}' has no apphost artifact.project. Supply a pre-published ImageIndexPath when the apphost manifest is unavailable.");
        }

        return Path.GetFullPath(project);
    }
}
