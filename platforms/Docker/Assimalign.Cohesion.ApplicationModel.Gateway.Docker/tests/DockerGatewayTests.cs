using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Name: Should be the stable identity 'docker'")]
    public void Name_OnGateway_ShouldBeDocker()
    {
        // Arrange
        var gateway = new DockerGateway();

        // Act
        string name = gateway.Name.Value;

        // Assert
        name.ShouldBe("docker");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - UseDockerGateway: Should apply Docker host and image archive arguments")]
    public void UseDockerGateway_OnPlatformArguments_ShouldApplyDockerOptions()
    {
        // Arrange
        string image = $"registry.example/worker@sha256:{new string('a', 64)}";
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        builder.AddResource(CreateManifest(image));
        Uri? endpoint = null;
        string? archive = null;

        // Act
        IApplication application = builder
            .UseDockerGateway(
                [
                    "--docker-host=http://127.0.0.1:2375",
                    $"--image-archive={image}=C:\\images\\worker.tar",
                ],
                options =>
                {
                    endpoint = options.EngineEndpoint;
                    archive = options.ImageArchives[image];
                })
            .Build();

        // Assert
        application.Model.GatewayIdentity.Value.ShouldBe("docker");
        endpoint.ShouldBe(new Uri("http://127.0.0.1:2375", UriKind.Absolute));
        archive.ShouldBe("C:\\images\\worker.tar");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - UseDockerGateway: Should throw ArgumentNullException for null configure")]
    public void UseDockerGateway_OnNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(
            () => builder.UseDockerGateway((Action<DockerGatewayOptions>)null!));

        // Assert
        exception.ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Should resolve an application image index, bind its registry, and return the pulled image ID")]
    public async Task GatherAsync_OnApplicationImageIndex_ShouldPullDigestAndReturnImageId()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string repository = "team/worker";
            const string registry = "registry.example:5000";
            string digest = $"sha256:{new string('a', 64)}";
            string indexPath = WriteImageIndex(root, repository, digest, "<late-bound>");
            await using var engineServer = new FakeDockerEngine();
            var options = new DockerGatewayOptions
            {
                ContainerRegistry = registry,
                ExportDirectory = Path.Combine(root, "exports"),
                ImageIndexPath = indexPath,
            };
            var controller = new CapturingController();
            options.Controllers.Add(controller);
            var gateway = new DockerGateway(
                options,
                () => new DockerEngineClient(engineServer.Endpoint));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"{repository}@{digest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();
            IApplicationGateway control = gateway;

            // Act
            await control.StartAsync(application.Model, CancellationToken.None);

            try
            {
                // Assert
                string canonical = $"{registry}/{repository}@{digest}";
                engineServer.PulledImage.ShouldBe(canonical);
                DockerImageArtifact artifact = controller.Artifact
                    .ShouldBeOfType<DockerImageArtifact>();
                artifact.Repository.ShouldBe($"{registry}/{repository}");
                artifact.Digest.ShouldBe(digest);
                artifact.Tag.ShouldBe("test");
                artifact.ImageId.ShouldBe(engineServer.ImageIdAfterPull);
            }
            finally
            {
                await control.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Should reject an index entry that differs from the manifest artifact")]
    public async Task GatherAsync_OnMismatchedImageIndexEntry_ShouldRejectResourceBeforeEngineContact()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifestDigest = $"sha256:{new string('a', 64)}";
            string indexDigest = $"sha256:{new string('b', 64)}";
            string indexPath = WriteImageIndex(root, "team/worker", indexDigest, null);
            var gateway = new DockerGateway(
                new DockerGatewayOptions
                {
                    ExportDirectory = Path.Combine(root, "exports"),
                    ImageIndexPath = indexPath,
                },
                () => throw new InvalidOperationException("Engine must not be contacted."));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"team/worker@{manifestDigest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();

            // Act
            InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
                () => ((IApplicationGateway)gateway).StartAsync(application.Model));

            // Assert
            exception.Message.ShouldContain("entry for resource 'worker' declares", Case.Sensitive);
            exception.Message.ShouldContain(manifestDigest, Case.Sensitive);
            exception.Message.ShouldContain(indexDigest, Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Should reject an image index owned by another application")]
    public async Task GatherAsync_OnForeignApplicationImageIndex_ShouldRejectResourceBeforeEngineContact()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string digest = $"sha256:{new string('a', 64)}";
            string indexPath = WriteImageIndex(
                root,
                "team/worker",
                digest,
                null,
                application: "other");
            var gateway = new DockerGateway(
                new DockerGatewayOptions
                {
                    ExportDirectory = Path.Combine(root, "exports"),
                    ImageIndexPath = indexPath,
                },
                () => throw new InvalidOperationException("Engine must not be contacted."));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"team/worker@{digest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();

            // Act
            InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
                () => ((IApplicationGateway)gateway).StartAsync(application.Model));

            // Assert
            exception.Message.ShouldContain("belongs to application 'other'", Case.Sensitive);
            exception.Message.ShouldContain("application 'appa'", Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Late-bound index entries should require a registry or archive")]
    public async Task GatherAsync_OnUnboundRegistryWithoutArchive_ShouldRejectResourceBeforeEngineContact()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string digest = $"sha256:{new string('a', 64)}";
            string indexPath = WriteImageIndex(root, "team/worker", digest, "<late-bound>");
            var gateway = new DockerGateway(
                new DockerGatewayOptions
                {
                    ExportDirectory = Path.Combine(root, "exports"),
                    ImageIndexPath = indexPath,
                },
                () => throw new InvalidOperationException("Engine must not be contacted."));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"team/worker@{digest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();

            // Act
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => ((IApplicationGateway)gateway).StartAsync(application.Model));

            // Assert
            exception.Message.ShouldContain("ContainerRegistry is absent", Case.Sensitive);
            exception.Message.ShouldContain("no archivePath is available", Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Build: Image index should reject a tag-only manifest even with a custom realizer")]
    public void Build_OnTagOnlyManifestWithImageIndexAndRealizer_ShouldRejectResource()
    {
        // Arrange
        var gateway = new DockerGateway(new DockerGatewayOptions
        {
            ImageIndexPath = "application.images.json",
            ImageRealizer = new UnusedImageRealizer(),
        });
        IApplicationBuilder builder = Application.CreateBuilder(
            ApplicationName.Parse("appa"),
            []);
        builder.AddResource(CreateManifest("team/worker:latest"));
        builder.UseGateway(gateway);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("@sha256:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Build: Should reject a tag-only manifest with a custom realizer")]
    public void Build_OnTagOnlyManifestWithRealizer_ShouldRejectResource()
    {
        // Arrange
        var gateway = new DockerGateway(new DockerGatewayOptions
        {
            ImageRealizer = new UnusedImageRealizer(),
        });
        IApplicationBuilder builder = Application.CreateBuilder(
            ApplicationName.Parse("appa"),
            []);
        builder.AddResource(CreateManifest("team/worker:latest"));
        builder.UseGateway(gateway);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("@sha256:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Image index should reject a different custom-realizer artifact")]
    public async Task GatherAsync_OnDifferentCustomRealizerArtifact_ShouldRejectResult()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string repository = "team/worker";
            string digest = $"sha256:{new string('a', 64)}";
            string otherDigest = $"sha256:{new string('b', 64)}";
            string indexPath = WriteImageIndex(root, repository, digest, registry: null);
            await using var engineServer = new FakeDockerEngine();
            var gateway = new DockerGateway(
                new DockerGatewayOptions
                {
                    ExportDirectory = Path.Combine(root, "exports"),
                    ImageIndexPath = indexPath,
                    ImageRealizer = new FixedImageRealizer($"{repository}@{otherDigest}"),
                },
                () => new DockerEngineClient(engineServer.Endpoint));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"{repository}@{digest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();

            // Act
            InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
                () => ((IApplicationGateway)gateway).StartAsync(application.Model));

            // Assert
            exception.Message.ShouldContain($"{repository}@{otherDigest}", Case.Sensitive);
            exception.Message.ShouldContain($"{repository}@{digest}", Case.Sensitive);
            exception.Message.ShouldContain("image-index artifact", Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Gather: Should reject a custom-realizer image that differs from the manifest")]
    public async Task GatherAsync_OnDifferentCustomRealizerImageWithoutIndex_ShouldRejectResult()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-docker-realizer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string repository = "team/worker";
            string digest = $"sha256:{new string('a', 64)}";
            string otherDigest = $"sha256:{new string('b', 64)}";
            await using var engineServer = new FakeDockerEngine();
            var gateway = new DockerGateway(
                new DockerGatewayOptions
                {
                    ExportDirectory = Path.Combine(root, "exports"),
                    ImageRealizer = new FixedImageRealizer($"{repository}@{otherDigest}"),
                },
                () => new DockerEngineClient(engineServer.Endpoint));
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                []);
            builder.AddResource(CreateManifest($"{repository}@{digest}"));
            builder.UseGateway(gateway);
            IApplication application = builder.Build();

            // Act
            InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
                () => ((IApplicationGateway)gateway).StartAsync(application.Model));

            // Assert
            exception.Message.ShouldContain($"{repository}@{otherDigest}", Case.Sensitive);
            exception.Message.ShouldContain($"{repository}@{digest}", Case.Sensitive);
            exception.Message.ShouldContain("manifest artifact", Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Options: Container registry should be an authority")]
    [InlineData("https://registry.example")]
    [InlineData("registry.example/team")]
    [InlineData("")]
    public void Constructor_OnInvalidContainerRegistry_ShouldRejectOption(string registry)
    {
        // Arrange
        var options = new DockerGatewayOptions { ContainerRegistry = registry };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new DockerGateway(options));

        // Assert
        exception.ParamName.ShouldBe(nameof(DockerGatewayOptions.ContainerRegistry));
    }

    private static ResourceManifest CreateManifest(string image) => new()
    {
        Name = "worker",
        Kind = "Worker",
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
    };

    private static string WriteImageIndex(
        string root,
        string repository,
        string digest,
        string? registry,
        string application = "appa")
    {
        string indexPath = Path.Combine(root, "application.images.json");
        string registryJson = registry is null ? "null" : $"\"{registry}\"";
        File.WriteAllText(
            indexPath,
            $$"""
            {
              "schema": "cohesion/images/v1",
              "application": "{{application}}",
              "images": [
                {
                  "resource": "worker",
                  "repository": "{{repository}}",
                  "digest": "{{digest}}",
                  "tag": "test",
                  "aot": true,
                  "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0",
                  "registry": {{registryJson}}
                }
              ]
            }
            """);
        return indexPath;
    }

    private sealed class CapturingController : IApplicationResourceController
    {
        public IContainerImageArtifact? Artifact { get; private set; }

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Artifact = context.GetArtifact<IContainerImageArtifact>();
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) =>
            StopAsync(context, cancellationToken);
    }

    private sealed class UnusedImageRealizer : IImageRealizer
    {
        public Task<IContainerImageArtifact> RealizeAsync(
            ResourceId resource,
            string imageReference,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The invalid manifest must fail before realization.");
    }

    private sealed class FixedImageRealizer : IImageRealizer
    {
        private readonly string _imageReference;

        public FixedImageRealizer(string imageReference) => _imageReference = imageReference;

        public Task<IContainerImageArtifact> RealizeAsync(
            ResourceId resource,
            string imageReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ContainerImageArtifacts.Create(resource, _imageReference));
        }
    }
}
