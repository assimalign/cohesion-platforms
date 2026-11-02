using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// The engine an end-to-end test runs on, and a network of its own there whose containers, and the
/// network itself, the test removes when it ends.
/// </summary>
internal sealed class DockerEngine : IAsyncDisposable
{
    /// <summary>The variable that turns the end-to-end tests on: <c>1</c> runs them against the engine.</summary>
    public const string EnableVariable = "COHESION_DOCKER_E2E";

    /// <summary>The variable that names the engine, when it is not <c>DOCKER_HOST</c> or the platform's default.</summary>
    public const string EndpointVariable = "COHESION_DOCKER_ENDPOINT";

    /// <summary>The variable that names the fixture program's image, when it is not the default.</summary>
    public const string ImageVariable = "COHESION_DOCKER_WEB_IMAGE";

    /// <summary>The variable that keeps the test's containers and network when it ends, to inspect a failed run.</summary>
    public const string KeepVariable = "COHESION_DOCKER_KEEP_NETWORK";

    private const string defaultImage = "cohesion-docker-web:e2e";

    private DockerEngine(string? endpoint)
    {
        Endpoint = endpoint;
        Network = string.Concat("cohesion-e2e-", Guid.NewGuid().ToString("N").AsSpan(0, 8));
        Image = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : defaultImage;
        Client = new DockerEngineClient(DockerEngineEndpoint.Resolve(endpoint));
    }

    /// <summary>The engine's endpoint; <see langword="null"/> resolves it as the docker CLI does.</summary>
    public string? Endpoint { get; }

    public string Network { get; }

    /// <summary>The fixture program's image, which the engine has in its store.</summary>
    public string Image { get; }

    /// <summary>The test's own client of the engine, apart from the gateway's.</summary>
    public DockerEngineClient Client { get; }

    public static DockerEngine Connect()
    {
        return new DockerEngine(Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } endpoint ? endpoint : null);
    }

    /// <summary>
    /// Points the gateway's infrastructure at the engine and the test's network.
    /// </summary>
    public DockerInfrastructureBuilder Configure(DockerInfrastructureBuilder docker)
    {
        // The fixture image is loaded into the engine's store, never pulled.
        docker.Options.Endpoint = Endpoint;
        docker.Options.Network = Network;
        docker.Options.ImagePullPolicy = "Never";
        return docker;
    }

    /// <summary>
    /// Creates an infrastructure of the test's own over the same network, apart from the one the
    /// gateway owns, such as to call each instance's control plane directly.
    /// </summary>
    public DockerInfrastructure CreateInfrastructure()
    {
        return Configure(new DockerInfrastructureBuilder()).Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(KeepVariable) is "1" or "true")
        {
            Client.Dispose();
            return;
        }

        // The gateway leaves what it deployed running, as a platform's workloads outlive their gateway;
        // the test removes its containers and its network.
        foreach (DockerContainerSummary container in await Client.ListContainersAsync(DockerWorkload.SelectNetwork(Network), CancellationToken.None))
        {
            await Client.RemoveContainerAsync(container.Id ?? DockerWorkload.GetName(container)!, CancellationToken.None);
        }

        await Client.RemoveNetworkAsync(Network, CancellationToken.None);
        Client.Dispose();
    }
}
