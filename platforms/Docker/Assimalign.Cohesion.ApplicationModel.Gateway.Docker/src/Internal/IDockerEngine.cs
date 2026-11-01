using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// The Docker Engine API calls the platform makes: images, one network, and containers. The real
/// one is <see cref="DockerEngineClient"/>; tests use a fake engine.
/// </summary>
internal interface IDockerEngine : IDisposable
{
    /// <summary>Inspects an image; <see langword="null"/> when the engine has none by that reference.</summary>
    Task<DockerImageInspectResponse?> InspectImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Pulls an image by its reference, tag or digest included.</summary>
    Task PullImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Inspects a network; <see langword="null"/> when there is none by that name.</summary>
    Task<DockerNetworkInspectResponse?> InspectNetworkAsync(string name, CancellationToken cancellationToken);

    /// <summary>Creates a network; one that another caller created first is not an error.</summary>
    Task CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Lists every container, running or not, that carries all of the labels.</summary>
    Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken);

    /// <summary>Inspects a container; <see langword="null"/> when there is none by that name or id.</summary>
    Task<DockerContainerInspectResponse?> InspectContainerAsync(string container, CancellationToken cancellationToken);

    /// <summary>Creates a container by name and returns its id.</summary>
    Task<string> CreateContainerAsync(string name, DockerContainerCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Starts a container; one already running is left as it is.</summary>
    Task StartContainerAsync(string container, CancellationToken cancellationToken);

    /// <summary>Reads the last lines a container wrote to its standard output and error, interleaved as the engine kept them.</summary>
    Task<string> ReadLogsAsync(string container, int lines, CancellationToken cancellationToken);

    /// <summary>Removes a container, stopping it first; one that is gone already is not an error.</summary>
    Task RemoveContainerAsync(string container, CancellationToken cancellationToken);
}
