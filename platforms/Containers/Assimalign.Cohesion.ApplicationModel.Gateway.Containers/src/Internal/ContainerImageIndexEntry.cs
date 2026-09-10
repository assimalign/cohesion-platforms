namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class ContainerImageIndexEntry : IContainerImageIndexEntry
{
    public ContainerImageIndexEntry(
        ResourceName resource,
        string repository,
        string digest,
        string? tag,
        string? archivePath,
        bool aot,
        string baseImage,
        string? registry)
    {
        Resource = resource;
        Repository = repository;
        Digest = digest;
        Tag = tag;
        ArchivePath = archivePath;
        Aot = aot;
        BaseImage = baseImage;
        Registry = registry;
    }

    public ResourceName Resource { get; }

    public string Repository { get; }

    public string Digest { get; }

    public string? Tag { get; }

    public string? ArchivePath { get; }

    public bool Aot { get; }

    public string BaseImage { get; }

    public string? Registry { get; }
}
