namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class ContainerImageArtifact : IContainerImageArtifact
{
    public ContainerImageArtifact(
        ResourceId resource,
        string repository,
        string digest,
        string? tag)
    {
        Resource = resource;
        Repository = repository;
        Digest = digest;
        Tag = tag;
    }

    public ResourceId Resource { get; }

    public string Repository { get; }

    public string Digest { get; }

    public string? Tag { get; }
}
