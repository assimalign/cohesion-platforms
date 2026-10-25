using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Keeps Docker deployment records as files in the gateway's state directory and names where a container reads its
/// copy.
/// </summary>
/// <remarks>
/// <para>
/// A container cannot read the gateway's file system, so the controller copies the record into
/// <see cref="InstanceDirectory"/> with the engine's archive API before the container starts, and the gateway copies a
/// management change into the running container. The resource polls the directory, so a copied change applies without
/// a restart.
/// </para>
/// <para>
/// The archive API writes files but never deletes them, so a layer the record no longer has is written as an empty
/// object, which the runtime contract counts as absent.
/// </para>
/// </remarks>
internal sealed class DockerResourceRecordStore : IResourceRecordStore
{
    internal const string InstanceDirectory = "/var/run/cohesion-config";
    private const string emptyLayer = "{}";
    // The runtime contract's layer file names (docs/RUNTIME_CONTRACT.md, "Deployment record").
    private const string deploymentFile = "deployment.json";
    private const string overrideFile = "override.json";

    private readonly FileResourceRecordStore _files;

    public DockerResourceRecordStore(string stateDirectory)
    {
        _files = FileResourceRecordStore.Create(stateDirectory);
    }

    public string GetInstanceDirectory(ApplicationName application, ResourceName resource) => InstanceDirectory;

    public ValueTask<ResourceRecord?> ReadAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default) =>
        _files.ReadAsync(application, resource, cancellationToken);

    public ValueTask<bool> TryWriteAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord record,
        string? expectedVersion,
        CancellationToken cancellationToken = default) =>
        _files.TryWriteAsync(application, resource, record, expectedVersion, cancellationToken);

    public ValueTask DeleteAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default) =>
        _files.DeleteAsync(application, resource, cancellationToken);

    // The files a container holds for a record: every layer the record has, and an empty object for each layer the
    // previously delivered record had and this one does not.
    public static IReadOnlyList<DockerFilePlan> CreateFiles(ResourceRecord record, ResourceRecord? previous)
    {
        ArgumentNullException.ThrowIfNull(record);
        var files = new List<DockerFilePlan>();
        Add(files, deploymentFile, record.Deployment, previous?.Deployment);
        foreach ((ApplicationName owner, ReadOnlyMemory<byte> layer) in record.Claims)
        {
            Add(files, ClaimFileName(owner), layer, null);
        }

        if (previous is not null)
        {
            foreach (ApplicationName owner in previous.Claims.Keys)
            {
                if (!record.Claims.ContainsKey(owner))
                {
                    files.Add(new DockerFilePlan(InstanceDirectory + "/" + ClaimFileName(owner), Encoding.UTF8.GetBytes(emptyLayer), Sensitive: false));
                }
            }
        }

        Add(files, overrideFile, record.Override, previous?.Override);
        return files;
    }

    private static void Add(List<DockerFilePlan> files, string name, ReadOnlyMemory<byte> layer, ReadOnlyMemory<byte>? previous)
    {
        if (!layer.IsEmpty)
        {
            files.Add(new DockerFilePlan(InstanceDirectory + "/" + name, layer.ToArray(), Sensitive: false));
        }
        else if (previous is ReadOnlyMemory<byte> delivered && !delivered.IsEmpty)
        {
            files.Add(new DockerFilePlan(InstanceDirectory + "/" + name, Encoding.UTF8.GetBytes(emptyLayer), Sensitive: false));
        }
    }

    private static string ClaimFileName(ApplicationName owner) =>
        "claims." + owner.ToString() + ".json";
}
