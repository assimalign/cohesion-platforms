namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class ContainerImageIndexEntry : IContainerImageIndexEntry
{
    public ContainerImageIndexEntry(
        ResourceName resource,
        string repository,
        string? registry,
        string? tag,
        string digest,
        string platform,
        bool aot,
        string baseImage,
        string? archive)
    {
        Resource = resource;
        Repository = repository;
        Registry = registry;
        Tag = tag;
        Digest = digest;
        Platform = platform;
        Aot = aot;
        BaseImage = baseImage;
        Archive = archive;
    }

    public ResourceName Resource { get; }

    public string Repository { get; }

    public string? Registry { get; }

    public string? Tag { get; }

    public string Digest { get; }

    public string Platform { get; }

    public bool Aot { get; }

    public string BaseImage { get; }

    public string? Archive { get; }
}
