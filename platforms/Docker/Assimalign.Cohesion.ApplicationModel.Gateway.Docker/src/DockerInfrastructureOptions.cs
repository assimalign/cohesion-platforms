namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Configures the engine a <see cref="DockerInfrastructure"/> runs containers on and the shape of what
/// it runs there.
/// </summary>
public sealed class DockerInfrastructureOptions
{
    /// <summary>
    /// Gets or sets the engine's API endpoint, in the docker CLI's form, such as
    /// <c>unix:///var/run/docker.sock</c>, <c>npipe:////./pipe/docker_engine</c>, or
    /// <c>tcp://127.0.0.1:2375</c>; <see langword="null"/> resolves it as the docker CLI does: the
    /// <c>DOCKER_HOST</c> variable, then the platform's default socket.
    /// </summary>
    /// <remarks>
    /// Podman's Docker-compatible API works at either: on Windows a Podman machine serves the
    /// <c>docker_engine</c> pipe, and on Linux its socket is <c>unix:///run/podman/podman.sock</c>
    /// (rootful) or <c>unix:///run/user/{uid}/podman/podman.sock</c> (rootless).
    /// </remarks>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the network the gateway runs its resources on; <c>cohesion</c> unless set.
    /// </summary>
    /// <remarks>
    /// A network belongs to one gateway: the gateway polls every instance container on it. Each
    /// resource's containers join it under the resource's name, so a dependent reaches the resource
    /// at that name.
    /// </remarks>
    public string Network { get; set; } = "cohesion";

    /// <summary>
    /// Gets or sets the port every resource's <c>http</c> endpoint listens on inside its container;
    /// 8080 unless set. The resource's <c>endpoint</c> output is <c>http://{name}:{port}</c> on the
    /// network, and the engine also publishes the port on a loopback port of its host.
    /// </summary>
    public int EndpointPort { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the port every instance serves its control plane on inside its container; 9090
    /// unless set. The engine publishes it on a loopback port of its host, where the gateway polls and
    /// calls the instance.
    /// </summary>
    public int ControlPlanePort { get; set; } = 9090;

    /// <summary>
    /// Gets or sets when the gateway pulls a resource's image: <c>IfNotPresent</c> (the default) pulls
    /// it when the engine has none by that reference, <c>Always</c> before every deployment, and
    /// <c>Never</c> fails a deployment whose image the engine does not have.
    /// </summary>
    public string ImagePullPolicy { get; set; } = "IfNotPresent";
}
