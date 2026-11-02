namespace Assimalign.Cohesion.ApplicationModel.Hosting.Docker.Internal;

// This repository generates no resource accessors, so the messages are constants in one place.
internal static class ErrorMessages
{
    public const string GatewayEndpointInvalid = "The Docker platform names the instance's gateway in the {0} variable as docker:///networks/{{network}}; the instance's environment holds '{1}'. A gateway that runs the instance on Docker sets it.";

    public const string ContainerNameMissing = "The Docker platform names the instance's container in the {0} variable, which the instance's environment does not set. A gateway that runs the instance on Docker sets it.";

    public const string PublicationRefused = "The gateway refused the instance's publication: {0}";

    public const string PublicationEmpty = "The gateway answered the instance's publication with no publication.";

    public const string PublicationUnanswered = "No gateway answered the instance's publication on network '{0}' within {1}. A gateway that runs the network's containers polls their control-plane ports while it runs.";

    public const string ClientAlreadyStarted = "The Docker gateway client already serves a control plane.";
}
