namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

// This repository generates no resource accessors, so the messages are constants in one place.
internal static class ErrorMessages
{
    public const string ProviderNameMissing = "A provider name is required.";

    public const string ResourceNameMissing = "A resource name is required.";

    public const string ImageMissing = "An image reference is required, such as cohesion-docker-web:e2e.";

    public const string ImageDuplicate = "Resource '{0}/{1}' already has an image.";

    public const string ImageUnbound = "Resource '{0}/{1}' has no image to run on Docker; bind one with AddImage.";

    public const string ImageMissingLocally = "The engine has no image '{0}' for resource '{1}/{2}', and the image pull policy is Never.";

    public const string ImagePullPolicyInvalid = "The image pull policy is IfNotPresent, Always, or Never; '{0}' is none of them.";

    public const string NetworkInvalid = "A network name is 1 to 63 lowercase ASCII letters, digits, or hyphens, starting and ending with a letter or digit; '{0}' is not one.";

    public const string PortInvalid = "A port is from 1 to 65535, and the endpoint and control-plane ports differ.";

    public const string ReplicasInvalid = "The docker 'replicas' extension of resource '{0}/{1}' is a whole number of at least 1.";

    public const string WorkloadFailed = "Container '{0}' of resource '{1}/{2}' cannot run: {3}";

    public const string WorkloadOutput = "{0} Its last output:{1}{2}";

    public const string ControlPlaneEndpointUnsupported = "The Docker instance client calls docker:///networks/{{network}}/containers/{{container}}:{{port}}/ control planes, not '{0}'.";

    public const string ControlPlaneUnpublished = "Container '{0}' does not publish its control-plane port {1} on its host, or is gone.";

    public const string InstanceUnanswered = "The control plane of instance {0} at '{1}' answered HTTP {2} without an action result.";

    public const string ChannelAlreadyStarted = "A gateway already polls the network's instances.";

    public const string EngineEndpointInvalid = "A Docker engine endpoint is unix:///path/to/socket, npipe:////./pipe/name, or tcp://host:port; '{0}' is none of them.";

    public const string EngineTlsUnsupported = "The Docker platform speaks to an engine over a Unix socket, a named pipe, or plain TCP; '{0}' asks for TLS or SSH, which it does not speak yet.";

    public const string EngineUnreachable = "The Docker engine at '{0}' cannot be reached. Start Docker, or the Podman machine, or name the engine with DockerInfrastructureOptions.Endpoint or the DOCKER_HOST variable.";

    public const string EngineRequestFailed = "The Docker engine answered {0} {1} with HTTP {2}.";

    public const string EngineRequestUnanswered = "The Docker engine did not answer {0} {1}.";

    public const string EngineAnswerInvalid = "The Docker engine's answer is not the JSON the API documents.";

    public const string EngineVersionInvalid = "The Docker engine reports API version '{0}', which is not a version.";

    public const string EngineVersionUnsupported = "The Docker engine speaks API versions {0} to {1}, and this client {2} to {3}.";
}
