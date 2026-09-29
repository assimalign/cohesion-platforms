using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class ContainerImageArtifactsTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact(DisplayName = "Cohesion Test [Containers] - Create: Should preserve digest-pinned image identity")]
    public void Create_OnDigestPinnedReference_ShouldPreserveIdentity()
    {
        // Arrange
        ResourceId resource = ResourceId.New();

        // Act
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            resource,
            $"registry.example.test/team/api@{Digest}",
            "preview");

        // Assert
        artifact.Resource.ShouldBe(resource);
        artifact.Repository.ShouldBe("registry.example.test/team/api");
        artifact.Digest.ShouldBe(Digest);
        artifact.Tag.ShouldBe("preview");
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Create: Should reject an invalid image reference")]
    [InlineData("registry.example.test/team/api:latest")]
    [InlineData("registry.example.test/team/api:latest@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("registry.example.test/team /api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("registry.example.test/team@api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("registry.example.test/team/api@sha256:1234")]
    [InlineData("registry.example.test/team/api@sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Create_OnInvalidReference_ShouldThrowArgumentException(string imageReference)
    {
        // Arrange
        ResourceId resource = ResourceId.New();

        // Act
        var exception = Should.Throw<ArgumentException>(
            () => ContainerImageArtifacts.Create(resource, imageReference));

        // Assert
        exception.ParamName.ShouldBe("imageReference");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Create: Should canonicalize digest hexadecimal casing")]
    public void Create_OnUppercaseDigest_ShouldCanonicalizeDigest()
    {
        ResourceId resource = ResourceId.New();
        string uppercase = "sha256:" + Digest["sha256:".Length..].ToUpperInvariant();

        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            resource,
            $"registry.example.test/team/api@{uppercase}");

        artifact.Digest.ShouldBe(Digest);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Create: Should reject an empty metadata tag")]
    public void Create_OnEmptyTag_ShouldRejectArtifact()
    {
        // Arrange
        string reference = $"registry.example.test/team/api@{Digest}";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ContainerImageArtifacts.Create(ResourceId.New(), reference, " "));

        // Assert
        exception.ParamName.ShouldBe("tag");
    }
}
