using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

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
[JsonSerializable(typeof(DockerVolumeCreateRequest))]
[JsonSerializable(typeof(DockerVolumeInspectResponse))]
[JsonSerializable(typeof(DockerContainerCreateRequest))]
[JsonSerializable(typeof(DockerContainerCreateResponse))]
[JsonSerializable(typeof(DockerContainerInspectResponse))]
[JsonSerializable(typeof(DockerExecCreateRequest))]
[JsonSerializable(typeof(DockerExecCreateResponse))]
[JsonSerializable(typeof(DockerExecStartRequest))]
[JsonSerializable(typeof(DockerExecInspectResponse))]
[JsonSerializable(typeof(DockerEventMessage))]
[JsonSerializable(typeof(DockerProgressMessage))]
internal sealed partial class DockerEngineJsonContext : JsonSerializerContext;
