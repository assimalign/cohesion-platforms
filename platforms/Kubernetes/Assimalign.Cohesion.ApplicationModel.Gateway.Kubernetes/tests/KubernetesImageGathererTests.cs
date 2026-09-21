using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesImageGathererTests
{
    private static readonly string _digest = $"sha256:{new string('a', 64)}";
    private static readonly string _image = $"example/test@{_digest}";

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Gather: Resolve source and package images through the Kind registry")]
    [InlineData(null)]
    [InlineData("package")]
    public async Task GatherAsync_OnLocalKindArchive_ShouldPushAndBindDigest(string? manifestImage)
    {
        using var directory = new TestDirectory();
        directory.CreateFile("web.tar");
        string path = await directory.WriteIndexAsync("appa", "example/test", _digest, "web.tar", null);
        var route = new RecordingRegistryRoute();
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path }, route);
        IApplicationResource resource = CreateResource(manifestImage is null ? null : _image);
        IContainerImageArtifact artifact = await gatherer.GatherAsync(resource, ArtifactRef.Self, true, CancellationToken.None);
        artifact.Repository.ShouldBe("localhost:5001/example/test");
        artifact.Digest.ShouldBe(_digest);
        route.Pushes.ShouldHaveSingleItem().ShouldBe($"localhost:5001/example/test@{_digest}");
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Gather: Honor pinned and configured registries")]
    [InlineData(null, "registry.test:5000", "registry.test:5000/example/test")]
    [InlineData("pinned.test", "ignored.test", "pinned.test/example/test")]
    public async Task GatherAsync_OnRegistrySelection_ShouldPreservePrecedence(string? pinned, string configured, string expected)
    {
        using var directory = new TestDirectory();
        string path = await directory.WriteIndexAsync("appa", "example/test", _digest, null, pinned);
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path, ContainerRegistry = configured }, new RecordingRegistryRoute());
        var artifact = await gatherer.GatherAsync(CreateResource(null), ArtifactRef.Self, true, CancellationToken.None);
        artifact.Repository.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Gather: Reject conflicting index identities")]
    [InlineData("other", "a")]
    [InlineData("appa", "b")]
    public async Task GatherAsync_OnConflictingIdentity_ShouldReject(string application, string digestCharacter)
    {
        using var directory = new TestDirectory();
        string path = await directory.WriteIndexAsync(application, "example/test", $"sha256:{new string(digestCharacter[0], 64)}", null, null);
        var route = new RecordingRegistryRoute();
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path }, route);
        await Should.ThrowAsync<InvalidDataException>(() => gatherer.GatherAsync(CreateResource(_image), ArtifactRef.Self, true, CancellationToken.None));
        route.Pushes.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Reject missing archive before pushing")]
    public async Task GatherAsync_OnMissingArchive_ShouldReject()
    {
        using var directory = new TestDirectory();
        string path = await directory.WriteIndexAsync("appa", "example/test", _digest, "missing.tar", null);
        var route = new RecordingRegistryRoute();
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path }, route);
        await Should.ThrowAsync<FileNotFoundException>(() => gatherer.GatherAsync(CreateResource(null), ArtifactRef.Self, true, CancellationToken.None));
        route.Pushes.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Development must not use the Local Kind route")]
    public async Task GatherAsync_OnNonLocalModel_ShouldNotProbeKind()
    {
        using var directory = new TestDirectory();
        string path = await directory.WriteIndexAsync("appa", "example/test", _digest, null, null);
        var route = new RecordingRegistryRoute();
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path, ContainerRegistry = "registry.test" }, route);
        var artifact = await gatherer.GatherAsync(CreateResource(null), ArtifactRef.Self, false, CancellationToken.None);
        artifact.Repository.ShouldBe("registry.test/example/test");
        route.Probes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Reject unbound registry outside Kind")]
    public async Task GatherAsync_OnUnboundRegistry_ShouldReject()
    {
        using var directory = new TestDirectory();
        string path = await directory.WriteIndexAsync("appa", "example/test", _digest, null, null);
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions { ImageIndexPath = path }, new RecordingRegistryRoute { IsKind = false });
        await Should.ThrowAsync<InvalidOperationException>(() => gatherer.GatherAsync(CreateResource(null), ArtifactRef.Self, true, CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Preserve a digest-pinned manifest without an index")]
    public async Task GatherAsync_OnPackageManifest_ShouldUseImage()
    {
        var gatherer = new KubernetesImageGatherer(new KubernetesGatewayOptions(), new RecordingRegistryRoute());
        var artifact = await gatherer.GatherAsync(CreateResource(_image), ArtifactRef.Self, false, CancellationToken.None);
        artifact.Repository.ShouldBe("example/test");
        artifact.Digest.ShouldBe(_digest);
    }

    private sealed class RecordingRegistryRoute : IKubernetesImageRegistryRoute
    {
        public bool IsKind { get; init; } = true;
        public int Probes { get; private set; }
        public List<string> Pushes { get; } = [];
        public Task<bool> IsKindAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Probes++; return Task.FromResult(IsKind); }
        public Task PushAsync(IContainerImageIndexEntry entry, string indexPath, string registry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pushes.Add($"{registry}/{entry.Repository}@{entry.Digest}");
            return Task.CompletedTask;
        }
    }

    private static IApplicationResource CreateResource(string? image)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
            ApplicationName.Parse("appa"),
            []);
        return builder.AddResource(new ResourceManifest
        {
            Name = "web",
            Kind = "Web",
            Application = "appa",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Assimalign.Cohesion.Test.Application",
                Image = image,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "http",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
        }).Resource;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cohesion-kubernetes-image-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            Path = ResolvePhysicalPath(path);
        }

        /// <summary>
        /// The root with every symbolic link resolved, the form archive paths come back in: on macOS
        /// <see cref="System.IO.Path.GetTempPath"/> returns <c>/var/folders/…</c>, a link to <c>/private/var/…</c>.
        /// </summary>
        public string Path { get; }

        private static string ResolvePhysicalPath(string path)
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            string root = System.IO.Path.GetPathRoot(fullPath)!;
            string current = root;
            foreach (string component in fullPath[root.Length..].Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, component);
                FileSystemInfo? target = Directory.Exists(current)
                    ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                    : null;
                if (target is not null)
                {
                    current = System.IO.Path.GetFullPath(target.FullName);
                }
            }

            return System.IO.Path.GetFullPath(current);
        }

        public string CreateFile(string relativePath)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        public async Task<string> WriteIndexAsync(
            string application,
            string repository,
            string digest,
            string? archive,
            string? registry)
        {
            string archiveProperty = archive is null ? string.Empty : $",\n                      \"archive\": \"{archive}\"";
            string registryJson = registry is null ? "null" : $"\"{registry}\"";
            string json = $$"""
                {
                  "schema": "{{ContainerImageIndexes.ApplicationSchema}}",
                  "application": "{{application}}",
                  "images": [
                    {
                      "resource": "web",
                      "repository": "{{repository}}",
                      "registry": {{registryJson}},
                      "tag": "latest",
                      "digest": "{{digest}}",
                      "platform": "linux/amd64",
                      "aot": false,
                      "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0"{{archiveProperty}}
                    }
                  ]
                }
                """;
            string path = System.IO.Path.Combine(Path, "application.images.json");
            await File.WriteAllTextAsync(path, json);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
