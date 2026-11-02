using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// An engine in memory: it keeps the containers the platform creates, with their labels and the host
/// ports it publishes them on, and the images and networks it has.
/// </summary>
internal sealed class FakeDockerEngine : IDockerEngine
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FakeContainer> _containers = new(StringComparer.Ordinal);
    private int _nextPort = 40000;

    public HashSet<string> Images { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Networks { get; } = new(StringComparer.Ordinal);

    public List<string> Pulled { get; } = [];

    /// <summary>The host port the next container's control-plane port is published on; ephemeral ports otherwise.</summary>
    public int? NextControlPlanePort { get; set; }

    /// <summary>Called with a container's name before the engine removes it, such as to report it meanwhile.</summary>
    public Func<string, Task>? Removing { get; set; }

    public IReadOnlyList<FakeContainer> Containers
    {
        get
        {
            lock (_lock)
            {
                return [.. _containers.Values];
            }
        }
    }

    public FakeContainer this[string name]
    {
        get
        {
            lock (_lock)
            {
                return _containers[name];
            }
        }
    }

    /// <summary>Adds a container, as an earlier gateway or a test left it.</summary>
    public FakeContainer Add(string name, IReadOnlyDictionary<string, string> labels, int controlPlanePort, int hostPort)
    {
        var container = new FakeContainer(name, new Dictionary<string, string>(labels, StringComparer.Ordinal), controlPlanePort, hostPort) { State = "running" };
        lock (_lock)
        {
            _containers[name] = container;
        }

        return container;
    }

    public Task<DockerImageInspectResponse?> InspectImageAsync(string image, CancellationToken cancellationToken)
    {
        return Task.FromResult(Images.Contains(image) ? new DockerImageInspectResponse { Id = image } : null);
    }

    public Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        Pulled.Add(image);
        Images.Add(image);
        return Task.CompletedTask;
    }

    public Task<DockerNetworkInspectResponse?> InspectNetworkAsync(string name, CancellationToken cancellationToken)
    {
        return Task.FromResult(Networks.Contains(name) ? new DockerNetworkInspectResponse { Name = name } : null);
    }

    public Task CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken)
    {
        Networks.Add(request.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<DockerContainerSummary> matching =
            [
                .. _containers.Values
                    .Where(container => labels.All(label => container.Labels.TryGetValue(label.Key, out string? value) && value == label.Value))
                    .Select(static container => container.Summarize()),
            ];
            return Task.FromResult(matching);
        }
    }

    public Task<DockerContainerInspectResponse?> InspectContainerAsync(string container, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_containers.TryGetValue(container, out FakeContainer? found) ? found.Inspect() : null);
        }
    }

    public Task<string> CreateContainerAsync(string name, DockerContainerCreateRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_containers.ContainsKey(name))
            {
                throw new InvalidOperationException($"A container named '{name}' exists.");
            }

            int controlPlane = request.ExposedPorts!.Keys.Select(static port => int.Parse(port.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture)).Max();
            int hostPort = NextControlPlanePort ?? _nextPort++;
            NextControlPlanePort = null;
            _containers[name] = new FakeContainer(name, request.Labels ?? [], controlPlane, hostPort) { Request = request, State = "created" };
            return Task.FromResult(name);
        }
    }

    public Task StartContainerAsync(string container, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _containers[container].State = "running";
        }

        return Task.CompletedTask;
    }

    public Task<string> ReadLogsAsync(string container, int lines, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_containers.TryGetValue(container, out FakeContainer? found) ? found.Logs : string.Empty);
        }
    }

    public async Task RemoveContainerAsync(string container, CancellationToken cancellationToken)
    {
        if (Removing is { } removing)
        {
            await removing.Invoke(container);
        }

        lock (_lock)
        {
            _containers.Remove(container);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// One container of the fake engine.
/// </summary>
internal sealed class FakeContainer
{
    public FakeContainer(string name, Dictionary<string, string> labels, int controlPlanePort, int hostPort)
    {
        Name = name;
        Labels = labels;
        ControlPlanePort = controlPlanePort;
        HostPort = hostPort;
    }

    public string Name { get; }

    public Dictionary<string, string> Labels { get; }

    public int ControlPlanePort { get; }

    public int HostPort { get; }

    public DockerContainerCreateRequest? Request { get; init; }

    public string State { get; set; } = "running";

    public int ExitCode { get; set; }

    public int RestartCount { get; set; }

    public string Logs { get; set; } = string.Empty;

    public DockerContainerSummary Summarize()
    {
        return new DockerContainerSummary
        {
            Id = Name,
            Names = ["/" + Name],
            Labels = Labels,
            State = State,
            Ports = [new DockerPortSummary { IP = "127.0.0.1", PrivatePort = ControlPlanePort, PublicPort = HostPort, Type = "tcp" }],
        };
    }

    public DockerContainerInspectResponse Inspect()
    {
        return new DockerContainerInspectResponse
        {
            Id = Name,
            Name = "/" + Name,
            RestartCount = RestartCount,
            State = new DockerContainerState { Status = State, Running = State == "running", ExitCode = ExitCode },
            Config = new DockerContainerConfig { Labels = Labels },
            NetworkSettings = new DockerNetworkSettings
            {
                Ports = new Dictionary<string, DockerPortBinding[]?>
                {
                    [$"{ControlPlanePort}/tcp"] = [new DockerPortBinding { HostIp = "127.0.0.1", HostPort = HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
                },
            },
        };
    }
}
