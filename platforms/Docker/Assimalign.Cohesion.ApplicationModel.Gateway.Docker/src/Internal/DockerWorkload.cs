using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Docker;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// What the Docker platform runs for a resource: one container per replica of its image on the
/// gateway's network, under the resource's name as a network alias, each receiving the runtime
/// contract as its environment and publishing its <c>http</c> and control-plane ports on loopback
/// ports of the engine's host.
/// </summary>
internal static class DockerWorkload
{
    /// <summary>The label every container and network the platform creates carries, with <see cref="ManagedByValue"/>.</summary>
    public const string ManagedByLabel = "com.assimalign.cohesion.managed-by";

    /// <summary>The value of <see cref="ManagedByLabel"/>.</summary>
    public const string ManagedByValue = "cohesion";

    /// <summary>The label that names the network, and so the gateway, a container belongs to.</summary>
    public const string NetworkLabel = "com.assimalign.cohesion.network";

    /// <summary>The label that names the resource a container runs.</summary>
    public const string ResourceLabel = "com.assimalign.cohesion.resource";

    /// <summary>The label that names the resource's provider.</summary>
    public const string ProviderLabel = "com.assimalign.cohesion.provider";

    /// <summary>The label of a container's deployment, which the deployment's id fills in.</summary>
    public const string DeploymentLabel = "com.assimalign.cohesion.deployment";

    /// <summary>The label that holds the revision a container runs.</summary>
    public const string RevisionLabel = "com.assimalign.cohesion.revision";

    /// <summary>The label that holds the workload a container runs, which decides whether a deployment changes it.</summary>
    public const string WorkloadLabel = "com.assimalign.cohesion.workload";

    /// <summary>The label that numbers a container among its resource's replicas, from 0.</summary>
    public const string ReplicaLabel = "com.assimalign.cohesion.replica";

    /// <summary>The extension that sets how many containers the resource runs, as a JSON number.</summary>
    public const string ReplicasExtension = "replicas";

    /// <summary>The address published ports bind to on the engine's host: loopback only.</summary>
    public const string PublishAddress = "127.0.0.1";

    /// <summary>The restarts of one container that make it a restart loop.</summary>
    public const int RestartLoopCount = 2;

    /// <summary>
    /// The version of the container this platform builds for a resource. Every revision includes it, so
    /// a change to what the platform builds updates the containers an earlier version created.
    /// </summary>
    public const int TemplateVersion = 1;

    /// <summary>
    /// Computes the revision a resource's containers would run: what the platform deploys that the
    /// model does not carry, its template version, the image, and both ports.
    /// </summary>
    /// <remarks>
    /// The gateway fingerprints the manifest, extensions included, itself, and settings are values a
    /// deployment delivers, not a new definition; neither is in the revision.
    /// </remarks>
    public static string GetRevision(string image, int endpointPort, int controlPlanePort)
    {
        var text = new StringBuilder()
            .Append(TemplateVersion.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(image).Append('\n')
            .Append(endpointPort.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(controlPlanePort.ToString(CultureInfo.InvariantCulture)).Append('\n');

        return Hash(text);
    }

    /// <summary>
    /// Computes the workload a resource's containers would run: the revision, the replica count, and
    /// the settings' values in ordinal order. Containers that run another workload are replaced.
    /// </summary>
    public static string GetWorkload(string revision, ApplicationResourceManifest resource, IReadOnlyDictionary<SettingName, string> settings)
    {
        var text = new StringBuilder()
            .Append(revision).Append('\n')
            .Append(GetReplicas(resource).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (KeyValuePair<SettingName, string> setting in settings.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal))
        {
            text.Append(setting.Key.Value).Append('=').Append(setting.Value).Append('\n');
        }

        return Hash(text);
    }

    /// <summary>
    /// The labels that select every container of a resource on the network.
    /// </summary>
    public static Dictionary<string, string> SelectResource(string network, ApplicationResourceManifest resource)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedByLabel] = ManagedByValue,
            [NetworkLabel] = network,
            [ProviderLabel] = resource.Provider.Value,
            [ResourceLabel] = resource.Name.Value,
        };
    }

    /// <summary>
    /// The labels that select every instance container on the network.
    /// </summary>
    public static Dictionary<string, string> SelectNetwork(string network)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedByLabel] = ManagedByValue,
            [NetworkLabel] = network,
        };
    }

    /// <summary>
    /// The name of a resource's container: the network, the resource, the workload it runs, and its
    /// replica, so a deployment's containers never collide with those of the one they replace.
    /// </summary>
    public static string GetContainerName(string network, ApplicationResourceManifest resource, string workload, int replica)
    {
        return string.Concat(network, "-", resource.Name.Value, "-", workload[..8], "-", replica.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds one container of the resource for one deployment of it.
    /// </summary>
    public static DockerContainerCreateRequest CreateContainer(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        string image,
        string revision,
        string workload,
        int replica,
        string name,
        string network,
        DockerInfrastructureOptions options)
    {
        Dictionary<string, string> labels = SelectResource(network, resource);
        labels[DeploymentLabel] = deployment.ToString();
        labels[RevisionLabel] = revision;
        labels[WorkloadLabel] = workload;
        labels[ReplicaLabel] = replica.ToString(CultureInfo.InvariantCulture);

        string http = Port(options.EndpointPort);
        string controlPlane = Port(options.ControlPlanePort);
        return new DockerContainerCreateRequest
        {
            Image = image,
            Env = CreateEnvironment(model, resource, settings, deployment, name, network, options),
            Labels = labels,
            ExposedPorts = new Dictionary<string, DockerEmptyObject>(StringComparer.Ordinal)
            {
                [http] = new DockerEmptyObject(),
                [controlPlane] = new DockerEmptyObject(),
            },
            HostConfig = new DockerHostConfig
            {
                NetworkMode = network,

                // The engine's init is the container's PID 1, so the program is not. A program that
                // is PID 1 cannot end itself with a default-action signal: one whose start fails with
                // an unhandled exception keeps running instead of exiting, and the watch never sees
                // the failure.
                Init = true,

                // The engine restarts a container whose program ends, as a Deployment restarts its
                // pods; the watch reports a container that keeps ending.
                RestartPolicy = new DockerRestartPolicy { Name = "unless-stopped" },

                // An ephemeral loopback port of the engine's host for each: the gateway polls and calls
                // the control plane there, and a developer reaches the endpoint there.
                PortBindings = new Dictionary<string, DockerPortBinding[]?>(StringComparer.Ordinal)
                {
                    [http] = [new DockerPortBinding { HostIp = PublishAddress, HostPort = string.Empty }],
                    [controlPlane] = [new DockerPortBinding { HostIp = PublishAddress, HostPort = string.Empty }],
                },
            },
            NetworkingConfig = new DockerNetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, DockerEndpointSettings>(StringComparer.Ordinal)
                {
                    [network] = new DockerEndpointSettings { Aliases = [resource.Name.Value] },
                },
            },
        };
    }

    /// <summary>
    /// The address the resource's dependents reach its <c>http</c> endpoint at: its name on the
    /// network, where every container of it answers.
    /// </summary>
    public static string GetEndpoint(ApplicationResourceManifest resource, DockerInfrastructureOptions options)
    {
        return string.Concat("http://", resource.Name.Value, ":", options.EndpointPort.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The number of containers the resource runs: its <c>replicas</c> extension, or 1.
    /// </summary>
    /// <exception cref="ArgumentException">The extension is not a whole number of at least 1.</exception>
    public static int GetReplicas(ApplicationResourceManifest resource)
    {
        foreach (ApplicationResourceExtension extension in resource.Extensions)
        {
            if (extension.Name.Value != ReplicasExtension)
            {
                continue;
            }

            if (extension.Value.ValueKind != JsonValueKind.Number || !extension.Value.TryGetInt32(out int replicas) || replicas < 1)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ReplicasInvalid, resource.Provider, resource.Name), nameof(resource));
            }

            return replicas;
        }

        return 1;
    }

    /// <summary>
    /// Reads why a container cannot run, as the engine reports it: it ended, the engine could not start
    /// it, or it keeps restarting; <see langword="null"/> while it runs.
    /// </summary>
    public static string? GetFailure(DockerContainerInspectResponse container)
    {
        DockerContainerState? state = container.State;
        if (state is null)
        {
            return null;
        }

        string exit = string.Concat(
            "exited with code ", state.ExitCode.ToString(CultureInfo.InvariantCulture),
            state.OomKilled ? " (out of memory)" : string.Empty,
            string.IsNullOrWhiteSpace(state.Error) ? string.Empty : string.Concat(": ", state.Error));

        if (state.Dead || state.Status is "exited" or "dead")
        {
            return exit;
        }

        if (container.RestartCount >= RestartLoopCount)
        {
            // While the engine restarts it, a container reports the exit of its current run, not the last.
            return string.Concat(
                "restarted ", container.RestartCount.ToString(CultureInfo.InvariantCulture), " times",
                state.ExitCode != 0 || state.OomKilled ? string.Concat("; it last ", exit) : string.Empty);
        }

        if (state.Status == "created" && !string.IsNullOrWhiteSpace(state.Error))
        {
            return string.Concat("the engine could not start it: ", state.Error);
        }

        return null;
    }

    /// <summary>
    /// Reads the host port the engine published a container port on, from an inspection.
    /// </summary>
    public static int? GetHostPort(DockerContainerInspectResponse container, int port)
    {
        if (container.NetworkSettings?.Ports is not { } ports || !ports.TryGetValue(Port(port), out DockerPortBinding[]? bindings) || bindings is null)
        {
            return null;
        }

        foreach (DockerPortBinding binding in bindings)
        {
            if (int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out int published) && published > 0)
            {
                return published;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the host port the engine published a container port on, from a listing.
    /// </summary>
    public static int? GetHostPort(DockerContainerSummary container, int port)
    {
        foreach (DockerPortSummary summary in container.Ports ?? [])
        {
            if (summary.PrivatePort == port && summary.PublicPort > 0 && summary.Type is null or "tcp")
            {
                return summary.PublicPort;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a container's name from a listing, which prefixes it with a slash.
    /// </summary>
    public static string? GetName(DockerContainerSummary container)
    {
        return container.Names is [{ } name, ..] ? name.TrimStart('/') : null;
    }

    private static string Port(int port)
    {
        return string.Concat(port.ToString(CultureInfo.InvariantCulture), "/tcp");
    }

    private static string Hash(StringBuilder text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    // What a platform hands a workload, as its environment: who it is, the deployment that started it,
    // its platform and where its gateway is, its container, where it listens, and its settings.
    private static string[] CreateEnvironment(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        string container,
        string network,
        DockerInfrastructureOptions options)
    {
        var environment = new List<string>
        {
            Variable(AppEnvironment.Variables.Application, model.Name.Value),
            Variable(AppEnvironment.Variables.Gateway, DockerProtocol.PlatformName),
            Variable(AppEnvironment.Variables.Resource, resource.Name.Value),
            Variable(AppEnvironment.Variables.ManifestEndpoint, DockerProtocol.CreateGatewayEndpoint(network).AbsoluteUri),
            Variable(AppEnvironment.Variables.Deployment, deployment.ToString()),
            Variable(AppEnvironment.Variables.Endpoint("http", "HOST"), "0.0.0.0"),
            Variable(AppEnvironment.Variables.Endpoint("http", "PORT"), options.EndpointPort.ToString(CultureInfo.InvariantCulture)),
            Variable(AppEnvironment.Variables.Endpoint("http", "SCHEME"), Uri.UriSchemeHttp),
            Variable(DockerProtocol.ControlPlanePortVariable, options.ControlPlanePort.ToString(CultureInfo.InvariantCulture)),
            Variable(DockerProtocol.ContainerNameVariable, container),
        };

        // The gateway program's environment name is its resources' too.
        if (AppEnvironment.GetValue(AppEnvironment.Variables.Environment) is { } name)
        {
            environment.Add(Variable(AppEnvironment.Variables.Environment, name));
        }

        foreach (KeyValuePair<SettingName, string> setting in settings.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal))
        {
            environment.Add(Variable(AppEnvironment.Variables.Configuration(setting.Key.Value), setting.Value));
        }

        return [.. environment];
    }

    private static string Variable(string name, string value)
    {
        return string.Concat(name, "=", value);
    }
}
