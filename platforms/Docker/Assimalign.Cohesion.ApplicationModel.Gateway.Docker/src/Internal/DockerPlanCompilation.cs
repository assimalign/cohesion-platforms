using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerPlanCompilation
{
    public DockerPlanCompilation(
        ApplicationName application,
        string owner,
        string planHash,
        string runtimeHash,
        DockerNetworkPlan network,
        IReadOnlyList<DockerVolumePlan> volumes,
        DockerContainerPlan container,
        IReadOnlyList<DockerFilePlan> files,
        IReadOnlyList<DockerProbePlan> probes,
        IReadOnlyList<string> warnings)
    {
        Application = application;
        Owner = owner;
        PlanHash = planHash;
        RuntimeHash = runtimeHash;
        Network = network;
        Volumes = Copy(volumes);
        Container = container;
        Files = Copy(files);
        Probes = Copy(probes);
        Warnings = Copy(warnings);
    }

    public ApplicationName Application { get; }

    public string Owner { get; }

    public string PlanHash { get; }

    public string RuntimeHash { get; }

    public DockerNetworkPlan Network { get; }

    public IReadOnlyList<DockerVolumePlan> Volumes { get; }

    public DockerContainerPlan Container { get; }

    public IReadOnlyList<DockerFilePlan> Files { get; }

    public IReadOnlyList<DockerProbePlan> Probes { get; }

    public IReadOnlyList<string> Warnings { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }
}

internal sealed record DockerNetworkPlan(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

internal sealed record DockerVolumePlan(
    string Name,
    string Claim,
    string Target,
    IReadOnlyDictionary<string, string> Labels);

internal sealed record DockerContainerPlan(
    string Name,
    string Image,
    string ImageReference,
    WorkloadKind Workload,
    int StopGraceSeconds,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> NetworkAliases,
    IReadOnlyList<DockerVolumeMountPlan> VolumeMounts,
    IReadOnlyList<DockerPortPublishPlan> PortBindings,
    IReadOnlyList<string> Tmpfs,
    string RestartPolicy);

internal sealed record DockerVolumeMountPlan(string Source, string Target);

internal sealed record DockerPortPublishPlan(
    string Endpoint,
    int ContainerPort,
    string Protocol,
    string HostIp,
    int? HostPort,
    bool ProbeOnly);

internal sealed record DockerFilePlan(
    string Path,
    ReadOnlyMemory<byte> Content,
    bool Sensitive);

internal sealed record DockerProbePlan(
    string Role,
    string? Endpoint,
    ProbeKind Kind,
    string? Scheme,
    int? ContainerPort,
    string? Protocol,
    string? Value,
    IReadOnlyList<string> Command);
