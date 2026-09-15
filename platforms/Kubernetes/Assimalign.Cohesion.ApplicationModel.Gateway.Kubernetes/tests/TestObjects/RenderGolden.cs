using System;
using System.IO;

using Shouldly;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

internal static class RenderGolden
{
    internal static void Verify(string actual, string relativePath)
    {
        // Export actual render output for an explicit fixture refresh, retaining the assertion.
        string? outputDirectory = Environment.GetEnvironmentVariable("COHESION_RENDER_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            string path = Path.Combine(outputDirectory, "Kubernetes", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);
        actual.ShouldBe(File.ReadAllText(fixture).Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
