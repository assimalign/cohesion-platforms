using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-containers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RootPath = ResolvePhysicalPath(path);
    }

    /// <summary>
    /// The root as the product reports paths under it: with every symbolic link resolved. On macOS
    /// <see cref="Path.GetTempPath"/> returns <c>/var/folders/…</c>, a link to <c>/private/var/…</c>,
    /// and archive paths resolved by the index come back in the physical form.
    /// </summary>
    public string RootPath { get; }

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        foreach (string component in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo? target = Directory.Exists(current)
                ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                : null;
            if (target is not null)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return Path.GetFullPath(current);
    }

    public string CreateDirectory(string relativePath)
    {
        string path = GetPath(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);

    public string WriteAllText(string relativePath, string content)
    {
        string path = GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
