namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

// This repository generates no resource accessors, so the messages are constants in one place.
internal static class ErrorMessages
{
    public const string ProviderNameMissing = "A provider name is required.";

    public const string ResourceNameMissing = "A resource name is required.";

    public const string ImageMissing = "An image reference is required, such as localhost:5001/orders-api:1.0.";

    public const string ImageDuplicate = "Resource '{0}/{1}' already has an image.";

    public const string ImageUnbound = "Resource '{0}/{1}' has no image to run on Kubernetes; bind one with AddImage.";

    public const string NamespaceInvalid = "A namespace is a DNS label: 1 to 63 lowercase ASCII letters, digits, or hyphens, starting and ending with a letter or digit; '{0}' is not one.";

    public const string PortInvalid = "A port is from 1 to 65535.";

    public const string ReplicasInvalid = "The kubernetes 'replicas' extension of resource '{0}/{1}' is a whole number of at least 1.";

    public const string WorkloadFailed = "Pod '{0}' of resource '{1}/{2}' cannot run: {3}: {4}";

    public const string ControlPlaneEndpointUnsupported = "The Kubernetes instance client calls kubernetes:///namespaces/{{namespace}}/pods/{{pod}}:{{port}}/ control planes, not '{0}'.";

    public const string InstanceUnanswered = "The control plane of instance {0} at '{1}' answered HTTP {2} without an action result.";

    public const string TransportAlreadyStarted = "A gateway already receives the namespace's publications.";
}
