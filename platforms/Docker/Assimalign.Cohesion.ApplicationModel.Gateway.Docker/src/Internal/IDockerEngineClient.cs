using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal interface IDockerEngineClient : IDisposable
{
    Task PingAsync(CancellationToken cancellationToken = default);

    Task<DockerVersionResponse> GetVersionAsync(
        CancellationToken cancellationToken = default);

    Task<DockerImageInspectResponse?> InspectImageAsync(
        string image,
        CancellationToken cancellationToken = default);

    Task LoadImageAsync(
        Stream imageArchive,
        bool quiet = true,
        CancellationToken cancellationToken = default);

    Task<DockerNetworkInspectResponse?> InspectNetworkAsync(
        string networkIdOrName,
        CancellationToken cancellationToken = default);

    Task<DockerNetworkCreateResponse> CreateNetworkAsync(
        DockerNetworkCreateRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveNetworkAsync(
        string networkIdOrName,
        CancellationToken cancellationToken = default);

    Task<DockerVolumeInspectResponse?> InspectVolumeAsync(
        string volumeName,
        CancellationToken cancellationToken = default);

    Task<DockerVolumeInspectResponse> CreateVolumeAsync(
        DockerVolumeCreateRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveVolumeAsync(
        string volumeName,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task<DockerContainerInspectResponse?> InspectContainerAsync(
        string containerIdOrName,
        CancellationToken cancellationToken = default);

    Task<DockerContainerCreateResponse> CreateContainerAsync(
        string? name,
        DockerContainerCreateRequest request,
        CancellationToken cancellationToken = default);

    Task StartContainerAsync(
        string containerIdOrName,
        CancellationToken cancellationToken = default);

    Task StopContainerAsync(
        string containerIdOrName,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);

    Task RemoveContainerAsync(
        string containerIdOrName,
        bool force = false,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default);

    Task PutArchiveAsync(
        string containerIdOrName,
        string containerPath,
        Stream archive,
        bool noOverwriteDirectoryWithNonDirectory = false,
        CancellationToken cancellationToken = default);

    Task<DockerExecCreateResponse> CreateExecAsync(
        string containerIdOrName,
        DockerExecCreateRequest request,
        CancellationToken cancellationToken = default);

    Task StartExecAsync(
        string execId,
        DockerExecStartRequest request,
        CancellationToken cancellationToken = default);

    Task<DockerExecInspectResponse?> InspectExecAsync(
        string execId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<DockerEventMessage> GetEventsAsync(
        DockerEventsQuery? query = null,
        CancellationToken cancellationToken = default);
}
