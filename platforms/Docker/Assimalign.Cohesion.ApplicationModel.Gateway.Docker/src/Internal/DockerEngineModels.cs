using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

// The subset of the Docker Engine API this platform uses, as Docker documents it and Podman's
// Docker-compatible API serves it. Unknown members are skipped on read and nulls are not written.

internal sealed class DockerEmptyObject;

internal sealed class DockerVersionResponse
{
    public string? Version { get; set; }

    public string? ApiVersion { get; set; }

    [JsonPropertyName("MinAPIVersion")]
    public string? MinimumApiVersion { get; set; }

    public string? Os { get; set; }

    public string? Arch { get; set; }
}

internal sealed class DockerImageInspectResponse
{
    public string? Id { get; set; }

    public string? Architecture { get; set; }
}

internal sealed class DockerNetworkCreateRequest
{
    public required string Name { get; init; }

    public string? Driver { get; init; }

    public Dictionary<string, string>? Labels { get; init; }
}

internal sealed class DockerNetworkCreateResponse
{
    public string? Id { get; set; }
}

internal sealed class DockerNetworkInspectResponse
{
    public string? Name { get; set; }

    public string? Id { get; set; }

    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerContainerSummary
{
    public string? Id { get; set; }

    public string[]? Names { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public string? State { get; set; }

    public DockerPortSummary[]? Ports { get; set; }
}

internal sealed class DockerPortSummary
{
    public string? IP { get; set; }

    public int PrivatePort { get; set; }

    public int PublicPort { get; set; }

    public string? Type { get; set; }
}

internal sealed class DockerContainerCreateRequest
{
    public required string Image { get; init; }

    public string? Hostname { get; init; }

    public string[]? Env { get; init; }

    public Dictionary<string, string>? Labels { get; init; }

    public Dictionary<string, DockerEmptyObject>? ExposedPorts { get; init; }

    public DockerHostConfig? HostConfig { get; init; }

    public DockerNetworkingConfig? NetworkingConfig { get; init; }
}

internal sealed class DockerContainerCreateResponse
{
    public string? Id { get; set; }

    public string[]? Warnings { get; set; }
}

internal sealed class DockerContainerInspectResponse
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public int RestartCount { get; set; }

    public DockerContainerState? State { get; set; }

    public DockerContainerConfig? Config { get; set; }

    public DockerNetworkSettings? NetworkSettings { get; set; }
}

internal sealed class DockerContainerConfig
{
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerContainerState
{
    public string? Status { get; set; }

    public bool Running { get; set; }

    public bool Restarting { get; set; }

    [JsonPropertyName("OOMKilled")]
    public bool OomKilled { get; set; }

    public bool Dead { get; set; }

    public int ExitCode { get; set; }

    public string? Error { get; set; }
}

internal sealed class DockerHostConfig
{
    public string? NetworkMode { get; set; }

    public Dictionary<string, DockerPortBinding[]?>? PortBindings { get; set; }

    public DockerRestartPolicy? RestartPolicy { get; set; }

    public bool? Init { get; set; }
}

internal sealed class DockerRestartPolicy
{
    public string? Name { get; set; }

    public int? MaximumRetryCount { get; set; }
}

internal sealed class DockerPortBinding
{
    public string? HostIp { get; set; }

    public string? HostPort { get; set; }
}

internal sealed class DockerNetworkingConfig
{
    public Dictionary<string, DockerEndpointSettings>? EndpointsConfig { get; init; }
}

internal sealed class DockerEndpointSettings
{
    public string[]? Aliases { get; set; }
}

internal sealed class DockerNetworkSettings
{
    public Dictionary<string, DockerPortBinding[]?>? Ports { get; set; }
}

internal sealed class DockerProgressMessage
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("errorDetail")]
    public DockerProgressErrorDetail? ErrorDetail { get; set; }
}

internal sealed class DockerProgressErrorDetail
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(DockerVersionResponse))]
[JsonSerializable(typeof(DockerImageInspectResponse))]
[JsonSerializable(typeof(DockerNetworkCreateRequest))]
[JsonSerializable(typeof(DockerNetworkCreateResponse))]
[JsonSerializable(typeof(DockerNetworkInspectResponse))]
[JsonSerializable(typeof(DockerContainerSummary[]))]
[JsonSerializable(typeof(DockerContainerCreateRequest))]
[JsonSerializable(typeof(DockerContainerCreateResponse))]
[JsonSerializable(typeof(DockerContainerInspectResponse))]
[JsonSerializable(typeof(DockerProgressMessage))]
internal sealed partial class DockerEngineJsonContext : JsonSerializerContext;
