using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class ContainerImageIndexesTests
{
    private const string validDigest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact(DisplayName = "Cohesion Test [Containers] - Image index: Should parse one validated image document")]
    public async Task ReadImageAsync_OnValidDocument_ShouldReturnImage()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("image.json", ImageDocument());

        // Act
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);

        // Assert
        image.Resource.ShouldBe((ResourceName)"api");
        image.Repository.ShouldBe("example/appa-api");
        image.Digest.ShouldBe(validDigest);
        image.Tag.ShouldBe("preview");
        image.ArchivePath.ShouldBe("images/api.oci.tar");
        image.Aot.ShouldBeTrue();
        image.BaseImage.ShouldBe("mcr.microsoft.com/dotnet/runtime-deps:10.0");
        image.Registry.ShouldBe(ContainerImageIndexes.LateBoundRegistry);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Application image index: Should parse images in declaration order")]
    public async Task ReadApplicationAsync_OnValidDocument_ShouldReturnApplicationImages()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument(
                ImageEntryDocument("api"),
                ImageEntryDocument("worker", registry: null, archivePath: null)));

        // Act
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            path,
            CancellationToken.None);

        // Assert
        index.Application.ShouldBe(ApplicationName.Parse("appa"));
        index.Images.Count.ShouldBe(2);
        index.Images[0].Resource.ShouldBe((ResourceName)"api");
        index.Images[1].Resource.ShouldBe((ResourceName)"worker");
        index.Images[1].Registry.ShouldBeNull();
        index.Images[1].ArchivePath.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should require the exact v1 schema")]
    [InlineData("cohesion/image/v1")]
    [InlineData("cohesion/images/v2")]
    [InlineData("Cohesion/images/v1")]
    public async Task ReadImageAsync_OnDifferentSchema_ShouldRejectDocument(string schema)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("image.json", ImageDocument(schema: schema));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(ContainerImageIndexes.Schema, Case.Sensitive);
        exception.Message.ShouldContain(schema, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should reject invalid digests")]
    [InlineData("sha256:1234")]
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task ReadImageAsync_OnInvalidDigest_ShouldRejectDocument(string digest)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("image.json", ImageDocument(digest: digest));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("sha256:", Case.Sensitive);
        exception.Message.ShouldContain("64 hexadecimal characters", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Application image index: Should reject duplicate resource ownership")]
    public async Task ReadApplicationAsync_OnDuplicateResources_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument(ImageEntryDocument("api"), ImageEntryDocument("api")));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadApplicationAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("api", Case.Sensitive);
        exception.Message.ShouldContain("more than once", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should require AOT and registry fields")]
    [InlineData(true, false, "aot")]
    [InlineData(false, true, "registry")]
    public async Task ReadImageAsync_OnMissingRequiredField_ShouldRejectDocument(
        bool omitAot,
        bool omitRegistry,
        string field)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            ImageDocument(includeAot: !omitAot, includeRegistry: !omitRegistry));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(field, Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should reject missing required string fields")]
    [InlineData("schema")]
    [InlineData("resource")]
    [InlineData("repository")]
    [InlineData("digest")]
    [InlineData("baseImage")]
    public async Task ReadImageAsync_OnMissingRequiredStringField_ShouldRejectDocument(
        string field)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            RemoveProperty(ImageDocument(), field));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(field, Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should reject empty entry fields")]
    [InlineData("resource")]
    [InlineData("tag")]
    [InlineData("archivePath")]
    [InlineData("baseImage")]
    public async Task ReadImageAsync_OnEmptyEntryField_ShouldRejectDocument(string field)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            SetProperty(ImageDocument(), field, " "));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(field, Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Image index: Should accept explicit null optional fields")]
    public async Task ReadImageAsync_OnNullOptionalFields_ShouldReturnNullMetadata()
    {
        // Arrange
        using var directory = new TestDirectory();
        string document = SetProperty(ImageDocument(), "tag", null);
        document = SetProperty(document, "archivePath", null);
        string path = directory.WriteAllText("image.json", document);

        // Act
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);

        // Assert
        image.Tag.ShouldBeNull();
        image.ArchivePath.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Application image index: Should require application and images")]
    [InlineData("application")]
    [InlineData("images")]
    public async Task ReadApplicationAsync_OnMissingRequiredField_ShouldRejectDocument(string field)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            RemoveProperty(ApplicationDocument(ImageEntryDocument("api")), field));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadApplicationAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(field, Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Application image index: Should reject an empty application")]
    public async Task ReadApplicationAsync_OnEmptyApplication_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            SetProperty(ApplicationDocument(ImageEntryDocument("api")), "application", " "));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadApplicationAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("application", Case.Sensitive);
        exception.Message.ShouldContain("non-empty", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Application image index: Should reject a null image entry")]
    public async Task ReadApplicationAsync_OnNullEntry_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument("null"));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadApplicationAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("images[0]", Case.Sensitive);
        exception.Message.ShouldContain("must not be null", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Application image index: Should accept an empty image array")]
    public async Task ReadApplicationAsync_OnEmptyImages_ShouldReturnEmptyIndex()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument());

        // Act
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            path,
            CancellationToken.None);

        // Assert
        index.Images.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Image index: Should reject an unknown registry marker")]
    public async Task ReadImageAsync_OnUnknownRegistryMarker_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            ImageDocument(registry: "registry.example.test"));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain(ContainerImageIndexes.LateBoundRegistry, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should reject invalid repository identities")]
    [InlineData("team/api:latest")]
    [InlineData("team/../api")]
    [InlineData("team/API")]
    [InlineData("team/-api")]
    [InlineData("team/api.")]
    [InlineData("team/api..worker")]
    [InlineData("team/api._worker")]
    public async Task ReadImageAsync_OnInvalidRepository_ShouldRejectDocument(string repository)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            ImageDocument(repository: repository, registry: null));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("repository", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Image index: Should exclude a registry from late-bound repository identity")]
    public async Task ReadImageAsync_OnLateBoundRepositoryWithRegistry_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "image.json",
            ImageDocument(repository: "registry.example.test/team/api"));

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("exclude a registry authority", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Image index: Should reject unknown document fields")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_OnUnknownField_ShouldRejectDocument(bool applicationDocument)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = applicationDocument
            ? directory.WriteAllText(
                "application.images.json",
                ApplicationDocument(ImageEntryDocument("api", includeUnknown: true)))
            : directory.WriteAllText("image.json", ImageDocument(includeUnknown: true));

        // Act
        InvalidDataException exception = applicationDocument
            ? await Should.ThrowAsync<InvalidDataException>(
                () => ContainerImageIndexes.ReadApplicationAsync(path, CancellationToken.None))
            : await Should.ThrowAsync<InvalidDataException>(
                () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("unknown", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Resolve: Should return only the owning resource for ArtifactRef.Self")]
    public async Task Resolve_OnOwnArtifact_ShouldReturnOwningImage()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument(ImageEntryDocument("api"), ImageEntryDocument("worker")));
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            path,
            CancellationToken.None);

        // Act
        IContainerImageIndexEntry image = ContainerImageIndexes.Resolve(
            index,
            (ResourceName)"worker",
            ArtifactRef.Self);

        // Assert
        image.Resource.ShouldBe((ResourceName)"worker");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Resolve: Should reject a missing own image")]
    public async Task Resolve_OnMissingResource_ShouldRejectLookup()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument(ImageEntryDocument("api")));
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            path,
            CancellationToken.None);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => ContainerImageIndexes.Resolve(
                index,
                (ResourceName)"worker",
                ArtifactRef.Self));

        // Assert
        exception.Message.ShouldContain("worker", Case.Sensitive);
        exception.Message.ShouldContain("own image", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Resolve: Should reject an artifact other than self")]
    public async Task Resolve_OnDifferentArtifactReference_ShouldRejectLookup()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "application.images.json",
            ApplicationDocument(ImageEntryDocument("api")));
        IApplicationImageIndex index = await ContainerImageIndexes.ReadApplicationAsync(
            path,
            CancellationToken.None);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => ContainerImageIndexes.Resolve(
                index,
                (ResourceName)"api",
                new ArtifactRef("peer")));

        // Assert
        exception.Message.ShouldContain("self", Case.Sensitive);
        exception.Message.ShouldContain("peer", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Archive path: Should resolve relative to the index document")]
    public async Task ResolveArchivePath_OnRelativePath_ShouldUseIndexDirectory()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("indexes/image.json", ImageDocument());
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);

        // Act
        string? archivePath = ContainerImageIndexes.ResolveArchivePath(path, image);

        // Assert
        archivePath.ShouldBe(Path.GetFullPath(
            Path.Combine(directory.RootPath, "indexes", "images", "api.oci.tar")));
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Archive path: Should reject traversal outside the index directory")]
    [InlineData("../api.oci.tar")]
    [InlineData("..\\api.oci.tar")]
    public async Task ResolveArchivePath_OnTraversal_ShouldRejectPath(string archivePath)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText(
            "indexes/image.json",
            ImageDocument(archivePath: archivePath.Replace("\\", "\\\\", StringComparison.Ordinal)));
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);

        // Act
        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => ContainerImageIndexes.ResolveArchivePath(path, image));

        // Assert
        exception.Message.ShouldContain("escapes index directory", Case.Sensitive);
        exception.Message.ShouldContain(archivePath, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Image index: Should reject a rooted archive path")]
    public async Task ReadImageAsync_OnRootedArchivePath_ShouldRejectDocument()
    {
        // Arrange
        using var directory = new TestDirectory();
        string rooted = Path.Combine(Path.GetPathRoot(directory.RootPath)!, "outside.tar");
        string document = ImageDocument().Replace(
            "\"images/api.oci.tar\"",
            JsonSerializer.Serialize(rooted),
            StringComparison.Ordinal);
        string path = directory.WriteAllText("image.json", document);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ContainerImageIndexes.ReadImageAsync(path, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("relative path", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Late-bound registry: Should create a digest-only pull artifact")]
    public async Task CreateArtifact_OnLateBoundRegistry_ShouldBindAuthority()
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("image.json", ImageDocument());
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);
        ResourceId resource = ResourceId.New();

        // Act
        IContainerImageArtifact artifact = ContainerImageIndexes.CreateArtifact(
            resource,
            image,
            "127.0.0.1:5012");

        // Assert
        artifact.Resource.ShouldBe(resource);
        artifact.Repository.ShouldBe("127.0.0.1:5012/example/appa-api");
        artifact.Digest.ShouldBe(validDigest);
        artifact.Tag.ShouldBe("preview");
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Late-bound registry: Should reject a non-authority binding")]
    [InlineData("https://registry.example.test")]
    [InlineData("registry.example.test/team")]
    [InlineData("registry.example.test?tenant=a")]
    [InlineData("registry.example.test:invalid")]
    [InlineData("registry.example.test:")]
    public async Task CreateArtifact_OnInvalidRegistryAuthority_ShouldRejectBinding(string registry)
    {
        // Arrange
        using var directory = new TestDirectory();
        string path = directory.WriteAllText("image.json", ImageDocument());
        IContainerImageIndexEntry image = await ContainerImageIndexes.ReadImageAsync(
            path,
            CancellationToken.None);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ContainerImageIndexes.CreateArtifact(ResourceId.New(), image, registry));

        // Assert
        exception.ParamName.ShouldBe("registry");
    }

    private static string ImageDocument(
        string schema = ContainerImageIndexes.Schema,
        string digest = validDigest,
        string? registry = ContainerImageIndexes.LateBoundRegistry,
        string? archivePath = "images/api.oci.tar",
        bool includeAot = true,
        bool includeRegistry = true,
        bool includeUnknown = false,
        string repository = "example/appa-api")
    {
        var properties = new List<string>
        {
            $"\"schema\":\"{schema}\"",
            "\"resource\":\"api\"",
            $"\"repository\":{JsonSerializer.Serialize(repository)}",
            "\"tag\":\"preview\"",
            $"\"digest\":\"{digest}\"",
        };
        if (archivePath is not null)
        {
            properties.Add($"\"archivePath\":\"{archivePath}\"");
        }

        if (includeAot)
        {
            properties.Add("\"aot\":true");
        }

        properties.Add("\"baseImage\":\"mcr.microsoft.com/dotnet/runtime-deps:10.0\"");
        if (includeRegistry)
        {
            properties.Add(registry is null ? "\"registry\":null" : $"\"registry\":\"{registry}\"");
        }

        if (includeUnknown)
        {
            properties.Add("\"unknown\":true");
        }

        return "{\n  " + string.Join(",\n  ", properties) + "\n}";
    }

    private static string ImageEntryDocument(
        string resource,
        string? registry = ContainerImageIndexes.LateBoundRegistry,
        string? archivePath = "images/api.oci.tar",
        bool includeUnknown = false)
    {
        var properties = new List<string>
        {
            $"\"resource\":\"{resource}\"",
            "\"repository\":\"example/appa-api\"",
            "\"tag\":\"preview\"",
            $"\"digest\":\"{validDigest}\"",
            "\"aot\":true",
            "\"baseImage\":\"mcr.microsoft.com/dotnet/runtime-deps:10.0\"",
            registry is null ? "\"registry\":null" : $"\"registry\":\"{registry}\"",
        };
        if (archivePath is not null)
        {
            properties.Add($"\"archivePath\":\"{archivePath}\"");
        }

        if (includeUnknown)
        {
            properties.Add("\"unknown\":true");
        }

        return "{ " + string.Join(", ", properties) + " }";
    }

    private static string ApplicationDocument(params string[] entries) =>
        $$"""
        {
          "schema": "{{ContainerImageIndexes.Schema}}",
          "application": "appa",
          "images": [
            {{string.Join(",\n    ", entries)}}
          ]
        }
        """;

    private static string RemoveProperty(string json, string property)
    {
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        document.Remove(property).ShouldBeTrue();
        return document.ToJsonString();
    }

    private static string SetProperty(string json, string property, string? value)
    {
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        document[property] = value;
        return document.ToJsonString();
    }
}
