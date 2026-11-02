namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Internal;

// This repository generates no resource accessors, so the messages are constants in one place.
internal static class ErrorMessages
{
    public const string GatewayEndpointInvalid = "The Kubernetes platform names the instance's gateway in the {0} variable as kubernetes:///namespaces/{{namespace}}; the instance's environment holds '{1}'. A gateway that runs the instance on Kubernetes sets it.";

    public const string PodNameMissing = "The Kubernetes platform names the instance's pod in the {0} variable, which the instance's environment does not set. A gateway that runs the instance on Kubernetes sets it through the downward API.";

    public const string PublicationRefused = "The gateway refused the instance's publication: {0}";

    public const string PublicationEmpty = "The gateway answered the instance's publication with no publication.";

    public const string PublicationUnanswered = "No gateway answered the instance's publication in namespace '{0}' within {1}. A gateway that runs the namespace's resources watches their Leases while it runs.";

    public const string ClientAlreadyStarted = "The Kubernetes gateway client already serves a control plane.";

    public const string ClusterUnavailable = "The instance runs outside a Kubernetes pod, and no kubeconfig names a cluster: neither a file the KUBECONFIG variable names nor the default kubeconfig exists. A pod reaches its cluster through its service account; a program run outside one needs a kubeconfig.";
}
