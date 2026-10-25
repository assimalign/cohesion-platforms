using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

// Deployment records and platform surfaces: records live in ConfigMaps in the application's namespace, and the
// autoscaler and disruption budget a resource declares are surfaces the management API changes until the next deployment.
public sealed partial class KubernetesGateway
{
    // Stamps the pod template's restart digest after a management write. Each reconcile states the same digest from
    // the record in force, so the two field managers agree on the value.
    private const string RestartFieldManager = "cohesion-restart";

    private static readonly IReadOnlyCollection<ResourceExtensionKey> _supportedExtensions =
        [KubernetesHpaSurface.Key, KubernetesPodDisruptionBudgetSurface.Key];

    /// <inheritdoc/>
    protected override IResourceRecordStore? RecordStore => _records;

    /// <inheritdoc/>
    protected override IReadOnlyCollection<ResourceExtensionKey> SupportedExtensions => _supportedExtensions;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>kubernetes/hpa</c> for a resource with scale bounds, and <c>kubernetes/pod-disruption-budget</c> for one that
    /// declares a budget.
    /// </remarks>
    protected override IReadOnlyList<ResourcePlatformSurface> GetPlatformSurfaces(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Resource is IExternalResource || descriptor.Plan is not ResourcePlan plan)
        {
            return Array.Empty<ResourcePlatformSurface>();
        }

        int? ceiling = descriptor.Resource is IManifestResource manifest ? manifest.Manifest.Lifecycle.MaxReplicas : null;
        var surfaces = new List<ResourcePlatformSurface>(2);
        if (KubernetesHpaSurface.Create(plan, ceiling) is ResourcePlatformSurface autoscaler)
        {
            surfaces.Add(autoscaler);
        }

        if (KubernetesPodDisruptionBudgetSurface.Create(plan) is ResourcePlatformSurface budget)
        {
            surfaces.Add(budget);
        }

        return surfaces;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The object is applied by the same field manager as the plan, with the value in force, so the plan's next
    /// apply in this generation states the same value.
    /// </remarks>
    protected override async ValueTask ApplyPlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlatformSurface surface,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(surface);
        ResourcePlan plan = descriptor.Plan!;
        NamespaceRegistration registration = FindNamespaceRegistration(model.Name.Value);
        string resource = KubernetesMetadata.ResourceName(plan.Resource);
        string planHash = KubernetesPlanCompiler.ComputePlanHash(plan);
        IKubernetesObject<V1ObjectMeta> compiled = surface.Key == KubernetesHpaSurface.Key
            ? KubernetesHpaSurface.Compile(plan, KubernetesHpaSurface.Read(value), registration.NamespaceName, resource, planHash, registration.Owner)
            : KubernetesPodDisruptionBudgetSurface.Compile(
                KubernetesPodDisruptionBudgetSurface.Read(value), registration.NamespaceName, resource, planHash, registration.Owner);
        _options.ApplyPatches(plan.Resource, compiled);
        KubernetesMetadata.RestoreRequiredMetadata(compiled.Metadata, resource, registration.NamespaceName, resource, planHash, registration.Owner);
        await _resources.ApplyAsync(compiled, force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>The autoscaler's current and desired replicas, or the budget's healthy pods and allowed disruptions.</remarks>
    protected override async ValueTask<JsonElement?> ObservePlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlatformSurface surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(surface);
        NamespaceRegistration registration = FindNamespaceRegistration(model.Name.Value);
        var metadata = new V1ObjectMeta
        {
            Name = KubernetesMetadata.ResourceName(descriptor.Resource.Name),
            NamespaceProperty = registration.NamespaceName,
        };
        IKubernetesObject<V1ObjectMeta> probe = surface.Key == KubernetesHpaSurface.Key
            ? new V2HorizontalPodAutoscaler { Metadata = metadata }
            : new V1PodDisruptionBudget { Metadata = metadata };
        return await _resources.ReadAsync(probe, cancellationToken).ConfigureAwait(false) switch
        {
            V2HorizontalPodAutoscaler autoscaler => KubernetesHpaSurface.Observe(autoscaler),
            V1PodDisruptionBudget budget => KubernetesPodDisruptionBudgetSurface.Observe(budget),
            _ => null,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The kubelet delivers overrides and claims through the projected record within about a minute. A section an
    /// instance applies only at startup needs new pods, so a change to what overrides and claims set for such sections
    /// stamps the pod template's <c>cohesion.io/restart-digest</c> annotation, which rolls the workload. A deployment
    /// stamps the same annotation through the compiled pod template, from <see cref="ResourceInputs.RestartDigest"/>.
    /// </remarks>
    protected override async ValueTask OnRecordChangedAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourceRecord previous,
        ResourceRecord current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ResourceSurface? surface = GetRuntimeSurface(model, descriptor);
        string? digest = current.GetRestartDigest(surface);
        if (string.Equals(previous.GetRestartDigest(surface), digest, StringComparison.Ordinal) ||
            descriptor.Plan is not ResourcePlan plan ||
            plan.Workload.Kind is WorkloadKind.Job)
        {
            return;
        }

        NamespaceRegistration registration = FindNamespaceRegistration(model.Name.Value);
        var metadata = new V1ObjectMeta
        {
            Name = KubernetesMetadata.ResourceName(plan.Resource),
            NamespaceProperty = registration.NamespaceName,
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesMetadata.OwnerAnnotation] = registration.Owner,
            },
        };
        IKubernetesObject<V1ObjectMeta> workload = plan.Workload.Kind switch
        {
            WorkloadKind.StatefulSet => new V1StatefulSet { Metadata = metadata },
            WorkloadKind.DaemonSet => new V1DaemonSet { Metadata = metadata },
            _ => new V1Deployment { Metadata = metadata },
        };
        if (await _resources.ReadAsync(workload, cancellationToken).ConfigureAwait(false) is null)
        {
            // Not deployed yet: its first pods read the record when they start.
            return;
        }

        // "add" replaces an existing member; the compiled pod template always carries annotations.
        string patch = "[{\"op\":\"add\",\"path\":\"/spec/template/metadata/annotations/" +
            KubernetesResourceRecordStore.RestartDigestAnnotation.Replace("/", "~1", StringComparison.Ordinal) + "\",\"value\":\"" +
            JsonEncodedText.Encode(digest ?? KubernetesResourceRecordStore.NoRestartDigest) + "\"}]";
        await _restarts.JsonPatchAsync(workload, patch, cancellationToken).ConfigureAwait(false);
    }
}
