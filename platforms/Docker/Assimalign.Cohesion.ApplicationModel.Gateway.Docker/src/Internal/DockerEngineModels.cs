using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerEmptyObject;

internal sealed class DockerVersionResponse
{
    public string? Version { get; set; }

    public string? ApiVersion { get; set; }

    [JsonPropertyName("MinAPIVersion")]
    public string? MinimumApiVersion { get; set; }

    public string? GitCommit { get; set; }

    public string? GoVersion { get; set; }

    public string? Os { get; set; }

    public string? Arch { get; set; }
}

internal sealed class DockerImageInspectResponse
{
    public string? Id { get; set; }

    public string[]? RepoTags { get; set; }

    public string[]? RepoDigests { get; set; }

    public string? Created { get; set; }

    public long Size { get; set; }

    public string? Architecture { get; set; }

    public string? Os { get; set; }

    public DockerContainerConfig? Config { get; set; }
}

internal sealed class DockerNetworkCreateRequest
{
    public required string Name { get; init; }

    public bool? CheckDuplicate { get; init; }

    public string? Driver { get; init; }

    public bool? Internal { get; init; }

    public bool? Attachable { get; init; }

    public bool? Ingress { get; init; }

    public bool? EnableIPv6 { get; init; }

    public DockerIpam? IPAM { get; init; }

    public Dictionary<string, string>? Options { get; init; }

    public Dictionary<string, string>? Labels { get; init; }
}

internal sealed class DockerNetworkCreateResponse
{
    public string? Id { get; set; }

    public string? Warning { get; set; }
}

internal sealed class DockerNetworkInspectResponse
{
    public string? Name { get; set; }

    public string? Id { get; set; }

    public string? Created { get; set; }

    public string? Scope { get; set; }

    public string? Driver { get; set; }

    public bool EnableIPv6 { get; set; }

    public bool Internal { get; set; }

    public bool Attachable { get; set; }

    public bool Ingress { get; set; }

    public DockerIpam? IPAM { get; set; }

    public Dictionary<string, DockerNetworkContainer>? Containers { get; set; }

    public Dictionary<string, string>? Options { get; set; }

    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerNetworkContainer
{
    public string? Name { get; set; }

    public string? EndpointID { get; set; }

    public string? MacAddress { get; set; }

    public string? IPv4Address { get; set; }

    public string? IPv6Address { get; set; }
}

internal sealed class DockerIpam
{
    public string? Driver { get; set; }

    public DockerIpamConfig[]? Config { get; set; }

    public Dictionary<string, string>? Options { get; set; }
}

internal sealed class DockerIpamConfig
{
    public string? Subnet { get; set; }

    public string? IPRange { get; set; }

    public string? Gateway { get; set; }

    public Dictionary<string, string>? AuxiliaryAddresses { get; set; }
}

internal sealed class DockerVolumeCreateRequest
{
    public required string Name { get; init; }

    public string? Driver { get; init; }

    public Dictionary<string, string>? DriverOpts { get; init; }

    public Dictionary<string, string>? Labels { get; init; }
}

internal sealed class DockerVolumeInspectResponse
{
    public string? Name { get; set; }

    public string? Driver { get; set; }

    public string? Mountpoint { get; set; }

    public string? CreatedAt { get; set; }

    public Dictionary<string, JsonElement>? Status { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public string? Scope { get; set; }

    public Dictionary<string, string>? Options { get; set; }
}

internal sealed class DockerContainerCreateRequest
{
    public string? Hostname { get; init; }

    public string? Domainname { get; init; }

    public string? User { get; init; }

    public bool? AttachStdin { get; init; }

    public bool? AttachStdout { get; init; }

    public bool? AttachStderr { get; init; }

    public Dictionary<string, DockerEmptyObject>? ExposedPorts { get; init; }

    public bool? Tty { get; init; }

    public bool? OpenStdin { get; init; }

    public bool? StdinOnce { get; init; }

    public string[]? Env { get; init; }

    public string[]? Cmd { get; init; }

    public DockerHealthConfig? Healthcheck { get; init; }

    public required string Image { get; init; }

    public Dictionary<string, DockerEmptyObject>? Volumes { get; init; }

    public string? WorkingDir { get; init; }

    public string[]? Entrypoint { get; init; }

    public bool? NetworkDisabled { get; init; }

    public string? MacAddress { get; init; }

    public Dictionary<string, string>? Labels { get; init; }

    public string? StopSignal { get; init; }

    public int? StopTimeout { get; init; }

    public string[]? Shell { get; init; }

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

    public string? Created { get; set; }

    public string? Path { get; set; }

    public string[]? Args { get; set; }

    public DockerContainerState? State { get; set; }

    public string? Image { get; set; }

    public string? Name { get; set; }

    public int RestartCount { get; set; }

    public string? Driver { get; set; }

    public string? Platform { get; set; }

    public DockerContainerConfig? Config { get; set; }

    public DockerHostConfig? HostConfig { get; set; }

    public DockerMount[]? Mounts { get; set; }

    public DockerNetworkSettings? NetworkSettings { get; set; }
}

internal sealed class DockerContainerConfig
{
    public string? Hostname { get; set; }

    public string? Domainname { get; set; }

    public string? User { get; set; }

    public bool AttachStdin { get; set; }

    public bool AttachStdout { get; set; }

    public bool AttachStderr { get; set; }

    public Dictionary<string, DockerEmptyObject>? ExposedPorts { get; set; }

    public bool Tty { get; set; }

    public bool OpenStdin { get; set; }

    public bool StdinOnce { get; set; }

    public string[]? Env { get; set; }

    public string[]? Cmd { get; set; }

    public DockerHealthConfig? Healthcheck { get; set; }

    public string? Image { get; set; }

    public Dictionary<string, DockerEmptyObject>? Volumes { get; set; }

    public string? WorkingDir { get; set; }

    public string[]? Entrypoint { get; set; }

    public bool NetworkDisabled { get; set; }

    public string? MacAddress { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public string? StopSignal { get; set; }

    public int? StopTimeout { get; set; }

    public string[]? Shell { get; set; }
}

internal sealed class DockerContainerState
{
    public string? Status { get; set; }

    public bool Running { get; set; }

    public bool Paused { get; set; }

    public bool Restarting { get; set; }

    [JsonPropertyName("OOMKilled")]
    public bool OomKilled { get; set; }

    public bool Dead { get; set; }

    public int Pid { get; set; }

    public int ExitCode { get; set; }

    public string? Error { get; set; }

    public string? StartedAt { get; set; }

    public string? FinishedAt { get; set; }

    public DockerHealthState? Health { get; set; }
}

internal sealed class DockerHealthConfig
{
    public string[]? Test { get; set; }

    public long? Interval { get; set; }

    public long? Timeout { get; set; }

    public long? StartPeriod { get; set; }

    public long? StartInterval { get; set; }

    public int? Retries { get; set; }
}

internal sealed class DockerHealthState
{
    public string? Status { get; set; }

    public int FailingStreak { get; set; }

    public DockerHealthLogEntry[]? Log { get; set; }
}

internal sealed class DockerHealthLogEntry
{
    public string? Start { get; set; }

    public string? End { get; set; }

    public int ExitCode { get; set; }

    public string? Output { get; set; }
}

internal sealed class DockerHostConfig
{
    public string[]? Binds { get; set; }

    public DockerLogConfig? LogConfig { get; set; }

    public string? NetworkMode { get; set; }

    public Dictionary<string, DockerPortBinding[]?>? PortBindings { get; set; }

    public DockerRestartPolicy? RestartPolicy { get; set; }

    public bool? AutoRemove { get; set; }

    public string? VolumeDriver { get; set; }

    public string[]? VolumesFrom { get; set; }

    public string[]? CapAdd { get; set; }

    public string[]? CapDrop { get; set; }

    public string[]? Dns { get; set; }

    public string[]? DnsOptions { get; set; }

    public string[]? DnsSearch { get; set; }

    public string[]? ExtraHosts { get; set; }

    public string[]? GroupAdd { get; set; }

    public string? IpcMode { get; set; }

    public string? PidMode { get; set; }

    public bool? Privileged { get; set; }

    public bool? PublishAllPorts { get; set; }

    public bool? ReadonlyRootfs { get; set; }

    public string[]? SecurityOpt { get; set; }

    public Dictionary<string, string>? StorageOpt { get; set; }

    public Dictionary<string, string>? Tmpfs { get; set; }

    public string? UsernsMode { get; set; }

    public long? ShmSize { get; set; }

    public Dictionary<string, string>? Sysctls { get; set; }

    public string? Runtime { get; set; }

    public string? Isolation { get; set; }

    public bool? Init { get; set; }

    public DockerMountRequest[]? Mounts { get; set; }
}

internal sealed class DockerLogConfig
{
    public string? Type { get; set; }

    public Dictionary<string, string>? Config { get; set; }
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

internal sealed class DockerMountRequest
{
    public required string Type { get; init; }

    public string? Source { get; init; }

    public required string Target { get; init; }

    public bool? ReadOnly { get; init; }

    public string? Consistency { get; init; }

    public DockerBindOptions? BindOptions { get; init; }

    public DockerVolumeOptions? VolumeOptions { get; init; }

    public DockerTmpfsOptions? TmpfsOptions { get; init; }
}

internal sealed class DockerMount
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? Source { get; set; }

    public string? Destination { get; set; }

    public string? Driver { get; set; }

    public string? Mode { get; set; }

    [JsonPropertyName("RW")]
    public bool ReadWrite { get; set; }

    public string? Propagation { get; set; }
}

internal sealed class DockerBindOptions
{
    public string? Propagation { get; init; }

    public bool? NonRecursive { get; init; }

    public bool? CreateMountpoint { get; init; }

    public bool? ReadOnlyNonRecursive { get; init; }

    public bool? ReadOnlyForceRecursive { get; init; }
}

internal sealed class DockerVolumeOptions
{
    public bool? NoCopy { get; init; }

    public Dictionary<string, string>? Labels { get; init; }

    public DockerVolumeDriverConfig? DriverConfig { get; init; }

    public string? Subpath { get; init; }
}

internal sealed class DockerVolumeDriverConfig
{
    public string? Name { get; init; }

    public Dictionary<string, string>? Options { get; init; }
}

internal sealed class DockerTmpfsOptions
{
    public long? SizeBytes { get; init; }

    public int? Mode { get; init; }

    public string[][]? Options { get; init; }
}

internal sealed class DockerNetworkingConfig
{
    public Dictionary<string, DockerEndpointSettings>? EndpointsConfig { get; init; }
}

internal sealed class DockerEndpointSettings
{
    public DockerEndpointIpamConfig? IPAMConfig { get; set; }

    public string[]? Links { get; set; }

    public string[]? Aliases { get; set; }

    public string? MacAddress { get; set; }

    public Dictionary<string, string>? DriverOpts { get; set; }

    public string? NetworkID { get; set; }

    public string? EndpointID { get; set; }

    public string? Gateway { get; set; }

    public string? IPAddress { get; set; }

    public int IPPrefixLen { get; set; }

    public string? IPv6Gateway { get; set; }

    public string? GlobalIPv6Address { get; set; }

    public int GlobalIPv6PrefixLen { get; set; }

    public string[]? DNSNames { get; set; }
}

internal sealed class DockerEndpointIpamConfig
{
    public string? IPv4Address { get; set; }

    public string? IPv6Address { get; set; }

    public string[]? LinkLocalIPs { get; set; }
}

internal sealed class DockerNetworkSettings
{
    public string? Bridge { get; set; }

    public string? SandboxID { get; set; }

    public Dictionary<string, DockerPortBinding[]?>? Ports { get; set; }

    public string? SandboxKey { get; set; }

    public string? EndpointID { get; set; }

    public string? Gateway { get; set; }

    public string? IPAddress { get; set; }

    public int IPPrefixLen { get; set; }

    public string? IPv6Gateway { get; set; }

    public string? GlobalIPv6Address { get; set; }

    public int GlobalIPv6PrefixLen { get; set; }

    public string? MacAddress { get; set; }

    public Dictionary<string, DockerEndpointSettings>? Networks { get; set; }
}

internal sealed class DockerExecCreateRequest
{
    public bool? AttachStdin { get; init; }

    public bool? AttachStdout { get; init; }

    public bool? AttachStderr { get; init; }

    public string? DetachKeys { get; init; }

    public bool? Tty { get; init; }

    public string[]? Env { get; init; }

    public required string[] Cmd { get; init; }

    public bool? Privileged { get; init; }

    public string? User { get; init; }

    public string? WorkingDir { get; init; }
}

internal sealed class DockerExecCreateResponse
{
    public string? Id { get; set; }

    public string[]? Warnings { get; set; }
}

internal sealed class DockerExecStartRequest
{
    public bool? Detach { get; init; }

    public bool? Tty { get; init; }

    public int[]? ConsoleSize { get; init; }
}

internal sealed class DockerExecInspectResponse
{
    [JsonPropertyName("ID")]
    public string? Id { get; set; }

    public bool Running { get; set; }

    public int ExitCode { get; set; }

    public DockerExecProcessConfig? ProcessConfig { get; set; }

    public bool OpenStdin { get; set; }

    public bool OpenStderr { get; set; }

    public bool OpenStdout { get; set; }

    public bool CanRemove { get; set; }

    [JsonPropertyName("ContainerID")]
    public string? ContainerId { get; set; }

    public string? DetachKeys { get; set; }

    public int Pid { get; set; }
}

internal sealed class DockerExecProcessConfig
{
    public bool Privileged { get; set; }

    public string? User { get; set; }

    public bool Tty { get; set; }

    public string? EntryPoint { get; set; }

    public string[]? Arguments { get; set; }
}

internal sealed class DockerEventsQuery
{
    public long? Since { get; init; }

    public long? Until { get; init; }

    public Dictionary<string, string[]>? Filters { get; init; }
}

internal sealed class DockerEventMessage
{
    public string? Type { get; set; }

    public string? Action { get; set; }

    public DockerEventActor? Actor { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("timeNano")]
    public long TimeNano { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }
}

internal sealed class DockerEventActor
{
    [JsonPropertyName("ID")]
    public string? Id { get; set; }

    public Dictionary<string, string>? Attributes { get; set; }
}

internal sealed class DockerProgressMessage
{
    [JsonPropertyName("stream")]
    public string? Stream { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("progress")]
    public string? Progress { get; set; }

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
