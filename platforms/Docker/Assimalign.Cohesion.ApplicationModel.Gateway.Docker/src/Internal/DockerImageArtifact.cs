namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerImageArtifact : IContainerImageArtifact
{
    public DockerImageArtifact(
        ResourceId resource,
        string repository,
        string digest,
        string? tag,
        string imageId)
    {
        Resource = resource;
        Repository = repository;
        Digest = digest;
        Tag = tag;
        ImageId = imageId;
    }

    public ResourceId Resource { get; }

    public string Repository { get; }

    public string Digest { get; }

    public string? Tag { get; }

    public string ImageId { get; }
}
