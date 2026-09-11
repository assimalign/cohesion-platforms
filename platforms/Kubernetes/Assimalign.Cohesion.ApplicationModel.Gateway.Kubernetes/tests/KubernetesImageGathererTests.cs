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

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should resolve own entry and preserve repository for a Kind archive")]
    public async Task GatherAsync_OnMatchingOwnEntry_ShouldLoadArchiveWithRepository()
    {
        // Arrange
        using var directory = new TestDirectory();
        string archivePath = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var options = new KubernetesGatewayOptions
        {
            ImageIndexPath = indexPath,
            ContainerRegistry = "localhost:5000",
        };
        var kindImages = new RecordingKindImageLoader();
        var archives = new RecordingArchiveVerifier();
        var gatherer = new KubernetesImageGatherer(options, kindImages, archives);
        IApplicationResource resource = CreateResource(_image);

        // Act
        IContainerImageArtifact artifact = await gatherer.GatherAsync(
            resource,
            ArtifactRef.Self,
            isDevelopment: true,
            CancellationToken.None);

        // Assert
        artifact.Resource.ShouldBe(resource.Id);
        artifact.Repository.ShouldBe("example/test");
        artifact.Digest.ShouldBe(_digest);
        artifact.Tag.ShouldBe("latest");
        KindImageLoad call = kindImages.Calls.ShouldHaveSingleItem();
        call.ArchivePath.ShouldBe(Path.GetFullPath(archivePath));
        call.Digest.ShouldBe(_digest);
        archives.Calls.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should bind late-bound registry outside Kind")]
    public async Task GatherAsync_OnLateBoundNonKindEntry_ShouldBindRegistry()
    {
        // Arrange
        using var directory = new TestDirectory();
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: null,
            registry: null);
        var options = new KubernetesGatewayOptions
        {
            ImageIndexPath = indexPath,
            ContainerRegistry = "localhost:5000",
        };
        var gatherer = new KubernetesImageGatherer(
            options,
            new RecordingKindImageLoader(KindImageLoadResult.NotKind),
            new RecordingArchiveVerifier());

        // Act
        IContainerImageArtifact artifact = await gatherer.GatherAsync(
            CreateResource(_image),
            ArtifactRef.Self,
            isDevelopment: false,
            CancellationToken.None);

        // Assert
        artifact.Repository.ShouldBe("localhost:5000/example/test");
        artifact.Digest.ShouldBe(_digest);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should preserve a pinned index registry")]
    public async Task GatherAsync_OnPinnedRegistry_ShouldIgnoreConfiguredRegistry()
    {
        // Arrange
        using var directory = new TestDirectory();
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: null,
            registry: "registry.example:5000");
        var options = new KubernetesGatewayOptions
        {
            ImageIndexPath = indexPath,
            ContainerRegistry = "other.example:5000",
        };
        var kindImages = new RecordingKindImageLoader();
        var gatherer = new KubernetesImageGatherer(
            options,
            kindImages,
            new RecordingArchiveVerifier());

        // Act
        IContainerImageArtifact artifact = await gatherer.GatherAsync(
            CreateResource(_image),
            ArtifactRef.Self,
            isDevelopment: true,
            CancellationToken.None);

        // Assert
        artifact.Repository.ShouldBe("registry.example:5000/example/test");
        kindImages.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should refuse an index for another application")]
    public async Task GatherAsync_OnDifferentIndexApplication_ShouldRejectIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        string indexPath = await directory.WriteIndexAsync(
            application: "other",
            repository: "example/test",
            digest: _digest,
            archive: null,
            registry: null);
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            new RecordingKindImageLoader(),
            new RecordingArchiveVerifier());

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: false,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("belongs to application 'other'", Case.Sensitive);
        exception.Message.ShouldContain("manifest application 'appa'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should refuse an index identity that differs from the manifest")]
    public async Task GatherAsync_OnDifferentIndexDigest_ShouldRejectIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        string indexedDigest = $"sha256:{new string('b', 64)}";
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: indexedDigest,
            archive: null,
            registry: null);
        var kindImages = new RecordingKindImageLoader();
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            kindImages,
            new RecordingArchiveVerifier());

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: true,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(indexedDigest, Case.Sensitive);
        exception.Message.ShouldContain(_digest, Case.Sensitive);
        kindImages.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should refuse a missing advertised archive")]
    public async Task GatherAsync_OnMissingAdvertisedArchive_ShouldRejectIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/missing.tar",
            registry: null);
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            new RecordingKindImageLoader(),
            new RecordingArchiveVerifier());

        // Act
        FileNotFoundException exception = await Should.ThrowAsync<FileNotFoundException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: true,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("images", Case.Sensitive);
        exception.Message.ShouldContain("missing.tar", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should verify archive bytes before invoking Kind")]
    public async Task GatherAsync_OnCorruptArchive_ShouldRejectBeforeKindLoad()
    {
        // Arrange
        using var directory = new TestDirectory();
        _ = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var kindImages = new RecordingKindImageLoader();
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            kindImages,
            new RejectingArchiveVerifier());

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: true,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("digest verification failed", Case.Sensitive);
        kindImages.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should not load a Kind archive outside Development")]
    public async Task GatherAsync_OnNonDevelopmentModel_ShouldNotLoadKindArchive()
    {
        // Arrange
        using var directory = new TestDirectory();
        _ = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var kindImages = new RecordingKindImageLoader();
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions
            {
                ImageIndexPath = indexPath,
                ContainerRegistry = "registry.example:5000",
            },
            kindImages,
            new RecordingArchiveVerifier());

        // Act
        _ = await gatherer.GatherAsync(
            CreateResource(_image),
            ArtifactRef.Self,
            isDevelopment: false,
            CancellationToken.None);

        // Assert
        kindImages.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should refuse unresolved late binding outside a Kind archive path")]
    public async Task GatherAsync_OnLateBoundNonKindImageWithoutRegistry_ShouldRejectIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        _ = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var kindImages = new RecordingKindImageLoader(KindImageLoadResult.NotKind);
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            kindImages,
            new RecordingArchiveVerifier());

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: true,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(
            nameof(KubernetesGatewayOptions.ContainerRegistry),
            Case.Sensitive);
        kindImages.Calls.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should refuse unresolved late binding when Kind is unavailable")]
    public async Task GatherAsync_OnLateBoundImageWithUnavailableKind_ShouldRejectIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        _ = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions { ImageIndexPath = indexPath },
            new RecordingKindImageLoader(KindImageLoadResult.KindUnavailable),
            new RecordingArchiveVerifier());

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => gatherer.GatherAsync(
                CreateResource(_image),
                ArtifactRef.Self,
                isDevelopment: true,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(
            nameof(KubernetesGatewayOptions.ContainerRegistry),
            Case.Sensitive);
        exception.Message.ShouldContain("not acquired", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather image: Should use a registry when Kind is unavailable")]
    public async Task GatherAsync_OnLateBoundImageWithUnavailableKindAndRegistry_ShouldBindRegistry()
    {
        // Arrange
        using var directory = new TestDirectory();
        _ = directory.CreateFile(Path.Combine("images", "web.tar"));
        string indexPath = await directory.WriteIndexAsync(
            application: "appa",
            repository: "example/test",
            digest: _digest,
            archive: "images/web.tar",
            registry: null);
        var gatherer = new KubernetesImageGatherer(
            new KubernetesGatewayOptions
            {
                ImageIndexPath = indexPath,
                ContainerRegistry = "localhost:5000",
            },
            new RecordingKindImageLoader(KindImageLoadResult.KindUnavailable),
            new RecordingArchiveVerifier());

        // Act
        IContainerImageArtifact artifact = await gatherer.GatherAsync(
            CreateResource(_image),
            ArtifactRef.Self,
            isDevelopment: true,
            CancellationToken.None);

        // Assert
        artifact.Repository.ShouldBe("localhost:5000/example/test");
        artifact.Digest.ShouldBe(_digest);
    }

    private static IApplicationResource CreateResource(string image)
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

    private sealed class RecordingKindImageLoader : IKindImageLoader
    {
        private readonly KindImageLoadResult _result;

        public RecordingKindImageLoader(KindImageLoadResult result = KindImageLoadResult.Loaded) =>
            _result = result;

        public List<KindImageLoad> Calls { get; } = [];

        public Task<KindImageLoadResult> LoadIfKindAsync(
            string archivePath,
            string digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new KindImageLoad(archivePath, digest));
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingArchiveVerifier : IKubernetesImageArchiveVerifier
    {
        public int Calls { get; private set; }

        public Task VerifyAsync(
            IContainerImageIndexEntry entry,
            string imageIndexPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingArchiveVerifier : IKubernetesImageArchiveVerifier
    {
        public Task VerifyAsync(
            IContainerImageIndexEntry entry,
            string imageIndexPath,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidDataException("digest verification failed"));
    }

    private sealed record KindImageLoad(string ArchivePath, string Digest);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cohesion-kubernetes-image-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
