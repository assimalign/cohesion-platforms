using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerImageRealizerTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should reuse an existing digest and return immutable engine ID")]
    public async Task RealizeAsync_OnExistingDigest_ShouldReturnEngineImageIdWithoutLoadingArchive()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@sha256:{new string('a', 64)}";
        string imageId = $"sha256:{new string('b', 64)}";
        engineServer.AddImage(canonical, imageId);
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>());
        ResourceId resource = DockerTestContext.Create().Resource.Id;

        // Act
        IContainerImageArtifact result = await realizer.RealizeAsync(
            resource,
            canonical,
            CancellationToken.None);

        // Assert
        DockerImageArtifact docker = result.ShouldBeOfType<DockerImageArtifact>();
        docker.Resource.ShouldBe(resource);
        docker.ImageId.ShouldBe(imageId);
        engineServer.RecordedRequests.Select(request => request.RawUrl).ShouldBe(
        [
            "/version",
            $"/v1.51/images/{Uri.EscapeDataString(canonical)}/json",
        ]);
        engineServer.LoadedArchive.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should accept Docker Hub normalization when proving a digest")]
    public async Task RealizeAsync_OnNormalizedDockerHubDigest_ShouldReturnEngineImageId()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string digest = $"sha256:{new string('a', 64)}";
        string requested = $"team/worker@{digest}";
        string imageId = $"sha256:{new string('b', 64)}";
        engineServer.AddImage(
            requested,
            imageId,
            $"docker.io/team/worker@{digest}");
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>());

        // Act
        IContainerImageArtifact result = await realizer.RealizeAsync(
            DockerTestContext.Create().Resource.Id,
            requested,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<DockerImageArtifact>().ImageId.ShouldBe(imageId);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should pull a missing image by digest and verify the engine result")]
    public async Task RealizeAsync_OnMissingDigestWithoutArchive_ShouldPullAndVerifyEngineResult()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@sha256:{new string('a', 64)}";
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>());

        // Act
        IContainerImageArtifact result = await realizer.RealizeAsync(
            DockerTestContext.Create().Resource.Id,
            canonical,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<DockerImageArtifact>().ImageId.ShouldBe(engineServer.ImageIdAfterPull);
        engineServer.PulledImage.ShouldBe(canonical);
        engineServer.RecordedRequests.Select(request => request.Path).ShouldBe(
        [
            "/version",
            $"/images/{Uri.EscapeDataString(canonical)}/json",
            "/images/create",
            $"/images/{Uri.EscapeDataString(canonical)}/json",
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should reject a pulled image that does not prove the requested digest")]
    public async Task RealizeAsync_OnPulledDigestMismatch_ShouldRejectEngineImage()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine
        {
            RepoDigestsAfterPull =
            [
                $"registry.example/worker@sha256:{new string('f', 64)}",
            ],
        };
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@sha256:{new string('a', 64)}";
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>());

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => realizer.RealizeAsync(
                DockerTestContext.Create().Resource.Id,
                canonical,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("did not prove requested digest", Case.Sensitive);
        engineServer.PulledImage.ShouldBe(canonical);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should verify and load an OCI archive before accepting its digest")]
    public async Task RealizeAsync_OnMissingDigestWithArchive_ShouldLoadAndVerifyEngineResult()
    {
        // Arrange
        using var archive = new OciImageArchiveFixture();
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@{archive.Digest}";
        engineServer.ImageAvailableAfterLoad = archive.ImageId;
        engineServer.ImageIdAfterLoad = archive.ImageId;
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [canonical] = archive.Path,
            });

        // Act
        IContainerImageArtifact result = await realizer.RealizeAsync(
            DockerTestContext.Create().Resource.Id,
            canonical,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<DockerImageArtifact>().ImageId.ShouldBe(archive.ImageId);
        engineServer.LoadedArchive.ShouldBe(archive.Content);
        engineServer.RecordedRequests.Select(request => request.Method).ShouldBe(
            ["GET", "GET", "POST", "GET"]);
        DockerEngineRequest load = engineServer.RecordedRequests[2];
        load.RawUrl.ShouldBe("/v1.51/images/load?quiet=true");
        load.ContentType.ShouldBe("application/x-tar");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should verify and load a gzip-compressed OCI archive")]
    public async Task RealizeAsync_OnGzipOciArchive_ShouldLoadVerifiedCompressedBytes()
    {
        // Arrange
        using var archive = new OciImageArchiveFixture(gzip: true);
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@{archive.Digest}";
        engineServer.ImageAvailableAfterLoad = archive.ImageId;
        engineServer.ImageIdAfterLoad = archive.ImageId;
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [canonical] = archive.Path,
            });

        // Act
        IContainerImageArtifact result = await realizer.RealizeAsync(
            DockerTestContext.Create().Resource.Id,
            canonical,
            CancellationToken.None);

        // Assert
        result.ShouldBeOfType<DockerImageArtifact>().ImageId.ShouldBe(archive.ImageId);
        engineServer.LoadedArchive.ShouldBe(archive.Content);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should reject a corrupt archive layer before engine load")]
    public async Task RealizeAsync_OnCorruptArchiveLayer_ShouldRejectBeforeLoad()
    {
        // Arrange
        using var archive = new OciImageArchiveFixture(corruptLayer: true);
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@{archive.Digest}";
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [canonical] = archive.Path,
            });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => realizer.RealizeAsync(
                DockerTestContext.Create().Resource.Id,
                canonical,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(archive.LayerDigest, Case.Sensitive);
        exception.Message.ShouldContain("failed verification", Case.Sensitive);
        engineServer.LoadedArchive.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Image realizer: Should reject a loaded engine image whose ID differs from the verified archive")]
    public async Task RealizeAsync_OnLoadedImageWithDifferentImageId_ShouldRejectImage()
    {
        // Arrange
        using var archive = new OciImageArchiveFixture();
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        string canonical = $"registry.example/worker@{archive.Digest}";
        engineServer.ImageAvailableAfterLoad = archive.ImageId;
        engineServer.ImageIdAfterLoad = $"sha256:{new string('f', 64)}";
        var realizer = new DockerImageRealizer(
            engine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [canonical] = archive.Path,
            });

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => realizer.RealizeAsync(
                DockerTestContext.Create().Resource.Id,
                canonical,
                CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("does not match verified archive image ID", Case.Sensitive);
        engineServer.LoadedArchive.ShouldNotBeNull();
    }
}
