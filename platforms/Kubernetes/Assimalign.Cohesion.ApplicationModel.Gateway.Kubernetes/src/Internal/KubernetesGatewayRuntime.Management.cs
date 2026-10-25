using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed partial class KubernetesGatewayObservationRegistry
{
    private static ResourceInstanceInventory? CreateManagementInventory(Entry entry, IKubernetesObject<V1ObjectMeta> workload, IList<V1Pod> pods)
    {
        ResourceManifest? manifest = entry.Context.Model.Manifests.FirstOrDefault(candidate => candidate.Name == entry.Context.Resource.Name);
        if (manifest?.Runtime is not JsonElement runtime) { return null; }
        JsonElement management = runtime.GetProperty("management");
        string endpointName = management.GetProperty("endpoint").GetString()!;
        string path = management.GetProperty("path").GetString()!;
        PortBinding? port = entry.Context.Plan.Container.Ports.FirstOrDefault(candidate => candidate.Endpoint == endpointName);
        ResourceEndpoint server = entry.Compilation.Endpoints.FirstOrDefault(candidate => candidate.Name == endpointName);
        string? expectedOwner = ReadMetadata(entry.Workload.Metadata.Annotations, KubernetesMetadata.OwnerAnnotation);
        if (port is null || workload.Metadata?.Uid is not { Length: > 0 } uid ||
            expectedOwner is null || ReadMetadata(workload.Metadata.Annotations, KubernetesMetadata.OwnerAnnotation) != expectedOwner)
        {
            return new ResourceInstanceInventory(Guid.Empty, string.Empty, 0, false, Array.Empty<ResourceManagementTarget>());
        }
        Guid incarnation = new(SHA256.HashData(Encoding.UTF8.GetBytes(uid)).AsSpan(0, 16));
        int desired = workload switch
        {
            V1Deployment deployment => deployment.Spec?.Replicas ?? 1,
            V1StatefulSet stateful => stateful.Spec?.Replicas ?? 1,
            V1DaemonSet daemon => daemon.Status?.DesiredNumberScheduled ?? 0,
            V1Job job => job.Status?.CompletionTime is not null ? 0 : job.Spec?.Parallelism ?? 1,
            _ => 0
        };
        var targets = new List<ResourceManagementTarget>();
        bool addressesComplete = true;
        var generations = new List<string> { uid, desired.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (V1Pod pod in pods.OrderBy(static pod => pod.Metadata?.Uid, StringComparer.Ordinal))
        {
            if (pod.Metadata?.Uid is not { Length: > 0 } podId ||
                ReadMetadata(pod.Metadata.Labels, KubernetesMetadata.ResourceLabel) != entry.ResourceLabel ||
                ReadMetadata(pod.Metadata.Annotations, KubernetesMetadata.OwnerAnnotation) != expectedOwner) { continue; }
            V1ContainerStatus? process = pod.Status?.ContainerStatuses?.FirstOrDefault(status => status.Name == entry.Context.Plan.Container.Name);
            string generation = process?.ContainerID ?? string.Empty;
            bool terminating = pod.Metadata.DeletionTimestamp is not null || pod.Status?.Phase is "Succeeded" or "Failed";
            generations.Add(podId + "/" + generation + "/" + pod.Status?.PodIP + "/" + terminating);
            string scheme = string.IsNullOrWhiteSpace(port.Scheme) ? server.Scheme : port.Scheme;
            if (!Uri.TryCreateEndpoint(scheme, pod.Status?.PodIP, port.ContainerPort, path, out Uri? address))
            {
                if (!terminating) { addressesComplete = false; }
                continue;
            }
            targets.Add(new ResourceManagementTarget(podId, generation, address, terminating, server.Host, VerifyHostIdentity: true));
        }
        string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", generations))));
        bool complete = addressesComplete && targets.Count(static target => !target.IsTerminating) >= desired;
        return new ResourceInstanceInventory(incarnation, revision, desired, complete, targets.ToArray());
    }

    private static string? ReadMetadata(IDictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out string? value) ? value : null;
}
