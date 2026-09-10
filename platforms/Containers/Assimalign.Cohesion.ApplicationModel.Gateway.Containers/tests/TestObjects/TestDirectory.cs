using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-containers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

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
