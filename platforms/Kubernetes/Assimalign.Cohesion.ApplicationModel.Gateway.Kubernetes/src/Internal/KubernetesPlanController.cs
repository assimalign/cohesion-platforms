using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesPlanController : IApplicationResourceController
{
    private readonly KubernetesGatewayOptions _options;
    private readonly KubernetesPlanCompiler _compiler;
    private readonly IKubernetesResourceApi _resources;
    private readonly IKubernetesObservationRegistry _observations;
    private readonly HashSet<string> _reportedWarnings = new(StringComparer.Ordinal);

    public KubernetesPlanController(
        KubernetesGatewayOptions options,
        KubernetesPlanCompiler compiler,
        IKubernetesResourceApi resources,
        IKubernetesObservationRegistry observations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(observations);
        _options = options;
        _compiler = compiler;
        _resources = resources;
        _observations = observations;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            _compiler.Validate(plan);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            reason = exception.Message;
            return false;
        }

        reason = null;
        return true;
    }

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable mutation = await _observations
            .EnterMutationAsync(context, cancellationToken)
            .ConfigureAwait(false);
        _ = await ReconcileCoreAsync(
            context,
            registerObservation: true,
            ownEndpoints: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<KubernetesPlanCompilation> RefreshRuntimeEnvironmentAsync(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        IReadOnlyList<ResourceEndpoint> ownEndpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(ownEndpoints);
        cancellationToken.ThrowIfCancellationRequested();

        return await PatchRuntimeEnvironmentAsync(
            context,
            compilation,
            ownEndpoints,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<KubernetesPlanCompilation> ReconcileCoreAsync(
        IResourceControlContext context,
        bool registerObservation,
        IReadOnlyList<ResourceEndpoint>? ownEndpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        KubernetesPlanCompilation compilation = Compile(
            context,
            context.GetArtifact<IContainerImageArtifact>(),
            context.Inputs,
            ownEndpoints: ownEndpoints);
        ReportWarnings(compilation.Warnings);

        var existingObjects =
            new IKubernetesObject<V1ObjectMeta>?[compilation.Objects.Count];
        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> desired = compilation.Objects[index];
            IKubernetesObject<V1ObjectMeta>? existing = await _resources
                .ReadAsync(desired, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                RequireOwnership(existing, desired, context, "take ownership");
            }

            existingObjects[index] = existing;
        }

        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> staleObjects = await FindStaleObjectsAsync(
            context,
            compilation,
            cancellationToken).ConfigureAwait(false);

        StampRuntimeInputRevision(compilation, existingObjects);
        bool replaceStatefulSet = StampWorkloadRevisions(
            compilation,
            existingObjects,
            context.Model.Owner);
        if (replaceStatefulSet)
        {
            (_, int workloadIndex) = FindWorkload(compilation);
            if (existingObjects[workloadIndex] is V1StatefulSet existingStatefulSet)
            {
                RequireSafeStatefulSetControllerDeletion(
                    existingStatefulSet,
                    staleObjects);
            }
        }

        await AdoptPersistentVolumeClaimsAsync(
            context,
            compilation,
            staleObjects,
            cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> desired = compilation.Objects[index];
            IKubernetesObject<V1ObjectMeta>? existing = await RequireUnchangedAsync(
                desired,
                existingObjects[index],
                context,
                cancellationToken).ConfigureAwait(false);
            if (desired is V1Job desiredJob
                && existing is V1Job existingJob
                && RequiresJobReplacement(desiredJob, existingJob))
            {
                if (existingJob.Metadata?.DeletionTimestamp is null)
                {
                    await _resources.DeleteAsync(existingJob, cancellationToken).ConfigureAwait(false);
                }

                await WaitForDeletionAsync(desired, cancellationToken).ConfigureAwait(false);
                desired.Metadata.ResourceVersion = null;
                existing = null;
            }

            if (desired is V1StatefulSet
                && existing is V1StatefulSet existingStatefulSet
                && replaceStatefulSet)
            {
                if (existingStatefulSet.Metadata?.DeletionTimestamp is null)
                {
                    await _resources
                        .DeleteAsync(existingStatefulSet, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WaitForDeletionAsync(desired, cancellationToken).ConfigureAwait(false);
                desired.Metadata.ResourceVersion = null;
                existing = null;
            }

            await CreateOrApplyAsync(desired, existing, context, cancellationToken)
                .ConfigureAwait(false);
        }

        await VerifyStatefulClaimOwnershipAsync(context, compilation, cancellationToken)
            .ConfigureAwait(false);

        await DeleteStaleObjectsAsync(staleObjects, cancellationToken)
            .ConfigureAwait(false);
        if (registerObservation)
        {
            _observations.Register(context, compilation);
        }

        return compilation;
    }

    private async Task<KubernetesPlanCompilation> PatchRuntimeEnvironmentAsync(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        IReadOnlyList<ResourceEndpoint> ownEndpoints,
        CancellationToken cancellationToken)
    {
        (V1ConfigMap desiredConfigMap, int configMapIndex) =
            FindObject<V1ConfigMap>(compilation);
        (V1Secret desiredSecret, _) = FindObject<V1Secret>(compilation);
        (IKubernetesObject<V1ObjectMeta> desiredWorkload, int workloadIndex) =
            FindWorkload(compilation);

        Task<IKubernetesObject<V1ObjectMeta>?> configMapRead = _resources
            .ReadAsync(desiredConfigMap, cancellationToken);
        Task<IKubernetesObject<V1ObjectMeta>?> secretRead = _resources
            .ReadAsync(desiredSecret, cancellationToken);
        Task<IKubernetesObject<V1ObjectMeta>?> workloadRead = _resources
            .ReadAsync(desiredWorkload, cancellationToken);
        await Task.WhenAll(configMapRead, secretRead, workloadRead).ConfigureAwait(false);

        if (await configMapRead.ConfigureAwait(false) is not V1ConfigMap currentConfigMap)
        {
            throw new InvalidOperationException(
                $"Kubernetes ConfigMap/{desiredConfigMap.Metadata.Name} disappeared while " +
                "refreshing the public endpoint contract. Retry normal reconciliation first.");
        }

        IKubernetesObject<V1ObjectMeta>? currentSecret =
            await secretRead.ConfigureAwait(false);
        IKubernetesObject<V1ObjectMeta>? currentWorkload =
            await workloadRead.ConfigureAwait(false);
        if (currentWorkload is null)
        {
            throw new InvalidOperationException(
                $"Kubernetes workload '{desiredWorkload.Kind}/{desiredWorkload.Metadata.Name}' " +
                "disappeared while refreshing the public endpoint contract. Retry normal " +
                "reconciliation first.");
        }

        if (currentSecret is not V1Secret)
        {
            throw new InvalidOperationException(
                $"Kubernetes Secret/{desiredSecret.Metadata.Name} disappeared while refreshing " +
                "the public endpoint contract. Retry normal reconciliation first.");
        }

        RequireRuntimePatchShape(currentConfigMap);
        RequireRuntimePatchShape(currentSecret);
        RequireRuntimePatchShape(currentWorkload);

        RequireRuntimePatchOwnership(currentConfigMap, context.Model.Owner);
        RequireRuntimePatchOwnership(currentSecret, context.Model.Owner);
        RequireRuntimePatchOwnership(currentWorkload, context.Model.Owner);
        RequireCurrentRuntimePlan(currentConfigMap, desiredConfigMap);
        RequireCurrentRuntimePlan(currentSecret, desiredSecret);
        RequireCurrentRuntimePlan(currentWorkload, desiredWorkload);

        IReadOnlyDictionary<string, string> desiredPublicEnvironment =
            KubernetesPlanCompiler.CreatePublicEndpointEnvironment(context.Plan, ownEndpoints);
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < context.Plan.Exposures.Count; index++)
        {
            publicKeys.Add(ResourceEnvironment.Endpoint(
                context.Plan.Exposures[index].Endpoint,
                "PUBLIC_URL"));
        }

        var dataChanges = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string key in publicKeys)
        {
            string? currentValue = null;
            _ = currentConfigMap.Data?.TryGetValue(key, out currentValue);
            if (desiredPublicEnvironment.TryGetValue(key, out string? desiredValue))
            {
                if (!string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
                {
                    dataChanges[key] = desiredValue;
                }
            }
            else if (currentValue is not null)
            {
                dataChanges[key] = null;
            }
        }

        long currentRevision = Math.Max(
            ReadRevision(currentConfigMap.Metadata),
            Math.Max(
                ReadRevision(currentSecret?.Metadata),
                ReadRevision(GetPodTemplate(currentWorkload)?.Metadata)));
        long runtimeRevision = dataChanges.Count == 0
            ? Math.Max(1, currentRevision)
            : NextRevision(currentRevision);
        string runtimeValue = runtimeRevision.ToString(CultureInfo.InvariantCulture);

        V1ConfigMap refreshedConfigMap = KubernetesJson.Deserialize<V1ConfigMap>(
            KubernetesJson.Serialize(currentConfigMap));
        refreshedConfigMap.Data ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string? value) in dataChanges)
        {
            if (value is null)
            {
                refreshedConfigMap.Data.Remove(key);
            }
            else
            {
                refreshedConfigMap.Data[key] = value;
            }
        }

        SetAnnotation(
            refreshedConfigMap.Metadata,
            KubernetesMetadata.RuntimeInputRevisionAnnotation,
            runtimeValue);

        IKubernetesObject<V1ObjectMeta> refreshedWorkload = CloneWorkload(desiredWorkload);
        V1PodTemplateSpec refreshedTemplate = GetRequiredPodTemplate(refreshedWorkload);
        SetAnnotation(
            refreshedTemplate.Metadata,
            KubernetesMetadata.RuntimeInputRevisionAnnotation,
            runtimeValue);
        refreshedTemplate.Metadata.Annotations?.Remove(
            KubernetesMetadata.WorkloadRevisionAnnotation);
        string workloadRevision = ComputeWorkloadRevision(
            refreshedWorkload.Kind,
            refreshedTemplate);
        SetAnnotation(
            refreshedTemplate.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation,
            workloadRevision);
        SetAnnotation(
            refreshedWorkload.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation,
            workloadRevision);
        if (refreshedWorkload is V1Job refreshedJob)
        {
            SetAnnotation(
                refreshedJob.Metadata,
                KubernetesMetadata.JobSpecRevisionAnnotation,
                ComputeRevision(refreshedJob.Spec));
        }

        RequireCurrentWorkloadRevision(
            currentWorkload,
            desiredWorkload,
            workloadRevision);

        bool configMapRevisionChanged = !string.Equals(
            ReadAnnotation(
                currentConfigMap.Metadata,
                KubernetesMetadata.RuntimeInputRevisionAnnotation),
            runtimeValue,
            StringComparison.Ordinal);
        bool templateChanged = !string.Equals(
                ReadAnnotation(
                    GetPodTemplate(currentWorkload)?.Metadata,
                    KubernetesMetadata.RuntimeInputRevisionAnnotation),
                runtimeValue,
                StringComparison.Ordinal)
            || !string.Equals(
                ReadAnnotation(
                    GetPodTemplate(currentWorkload)?.Metadata,
                    KubernetesMetadata.WorkloadRevisionAnnotation),
                workloadRevision,
                StringComparison.Ordinal);
        bool workloadMetadataChanged = !string.Equals(
                ReadAnnotation(
                    currentWorkload.Metadata,
                    KubernetesMetadata.WorkloadRevisionAnnotation),
                workloadRevision,
                StringComparison.Ordinal);

        if (dataChanges.Count > 0 || configMapRevisionChanged)
        {
            string patch = CreateRuntimeConfigMapPatch(
                currentConfigMap,
                dataChanges,
                runtimeValue);
            await _resources
                .JsonPatchAsync(currentConfigMap, patch, cancellationToken)
                .ConfigureAwait(false);
        }

        if (templateChanged || workloadMetadataChanged)
        {
            if (templateChanged
                && currentWorkload is V1Job currentJob
                && refreshedWorkload is V1Job replacementJob)
            {
                await ReplaceRuntimeJobAsync(
                    currentJob,
                    replacementJob,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (currentWorkload is V1Job)
            {
                string patch = CreateRuntimeWorkloadMetadataPatch(
                    currentWorkload,
                    workloadRevision);
                await _resources
                    .JsonPatchAsync(currentWorkload, patch, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                string patch = CreateRuntimeWorkloadPatch(
                    currentWorkload,
                    runtimeValue,
                    workloadRevision);
                await _resources
                    .JsonPatchAsync(currentWorkload, patch, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var objects = new IKubernetesObject<V1ObjectMeta>[compilation.Objects.Count];
        for (int index = 0; index < objects.Length; index++)
        {
            objects[index] = compilation.Objects[index];
        }

        objects[configMapIndex] = refreshedConfigMap;
        objects[workloadIndex] = refreshedWorkload;
        return new KubernetesPlanCompilation(
            compilation.NamespaceName,
            compilation.PlanHash,
            objects,
            compilation.Readiness,
            compilation.Endpoints,
            compilation.Warnings);
    }

    private async Task ReplaceRuntimeJobAsync(
        V1Job current,
        V1Job replacement,
        CancellationToken cancellationToken)
    {
        await _resources.DeleteAsync(current, cancellationToken).ConfigureAwait(false);
        await WaitForDeletionAsync(current, cancellationToken).ConfigureAwait(false);
        PrepareForCreate(replacement.Metadata);
        if (!await _resources.TryCreateAsync(replacement, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Kubernetes Job/{replacement.Metadata.Name} was recreated concurrently while " +
                "refreshing its public endpoint contract. Retry reconciliation against the " +
                "latest object.");
        }
    }

    private static string CreateRuntimeConfigMapPatch(
        V1ConfigMap current,
        IReadOnlyDictionary<string, string?> dataChanges,
        string runtimeRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteJsonPatchOperation(
                writer,
                "test",
                "/metadata/resourceVersion",
                RequireRuntimePatchVersion(current));
            WriteJsonPatchOperation(
                writer,
                "add",
                $"/metadata/annotations/{EscapeJsonPointer(KubernetesMetadata.RuntimeInputRevisionAnnotation)}",
                runtimeRevision);
            foreach ((string key, string? value) in dataChanges)
            {
                WriteJsonPatchOperation(
                    writer,
                    value is null ? "remove" : "add",
                    $"/data/{EscapeJsonPointer(key)}",
                    value);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateRuntimeWorkloadPatch(
        IKubernetesObject<V1ObjectMeta> current,
        string runtimeRevision,
        string workloadRevision)
    {
        if (current is not V1Deployment and not V1StatefulSet and not V1DaemonSet)
        {
            throw new InvalidOperationException(
                $"Unsupported Kubernetes runtime patch kind '{current.Kind}'.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteJsonPatchOperation(
                writer,
                "test",
                "/metadata/resourceVersion",
                RequireRuntimePatchVersion(current));
            WriteJsonPatchOperation(
                writer,
                "add",
                $"/metadata/annotations/{EscapeJsonPointer(KubernetesMetadata.WorkloadRevisionAnnotation)}",
                workloadRevision);
            WriteJsonPatchOperation(
                writer,
                "add",
                $"/spec/template/metadata/annotations/{EscapeJsonPointer(KubernetesMetadata.RuntimeInputRevisionAnnotation)}",
                runtimeRevision);
            WriteJsonPatchOperation(
                writer,
                "add",
                $"/spec/template/metadata/annotations/{EscapeJsonPointer(KubernetesMetadata.WorkloadRevisionAnnotation)}",
                workloadRevision);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateRuntimeWorkloadMetadataPatch(
        IKubernetesObject<V1ObjectMeta> current,
        string workloadRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteJsonPatchOperation(
                writer,
                "test",
                "/metadata/resourceVersion",
                RequireRuntimePatchVersion(current));
            WriteJsonPatchOperation(
                writer,
                "add",
                $"/metadata/annotations/{EscapeJsonPointer(KubernetesMetadata.WorkloadRevisionAnnotation)}",
                workloadRevision);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonPatchOperation(
        Utf8JsonWriter writer,
        string operation,
        string path,
        string? value)
    {
        writer.WriteStartObject();
        writer.WriteString("op", operation);
        writer.WriteString("path", path);
        if (value is not null)
        {
            writer.WriteString("value", value);
        }

        writer.WriteEndObject();
    }

    private static string RequireRuntimePatchVersion(
        IKubernetesObject<V1ObjectMeta> current) =>
        !string.IsNullOrWhiteSpace(current.Metadata?.ResourceVersion)
            ? current.Metadata.ResourceVersion
            : throw new InvalidOperationException(
                $"Kubernetes object '{current.Kind}/{current.Metadata?.Name}' cannot be patched " +
                "without an API-server resourceVersion precondition.");

    private static string EscapeJsonPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static void RequireRuntimePatchShape(
        IKubernetesObject<V1ObjectMeta> current)
    {
        _ = RequireRuntimePatchVersion(current);
        bool isWorkload = current is V1Deployment or V1StatefulSet or V1DaemonSet or V1Job;
        V1PodTemplateSpec? template = GetPodTemplate(current);
        if (current.Metadata.DeletionTimestamp is not null
            || current.Metadata.Annotations is null
            || current is V1ConfigMap { Data: null }
            || (current is V1Secret
                && ReadAnnotation(
                    current.Metadata,
                    KubernetesMetadata.RuntimeInputRevisionAnnotation) is null)
            || (isWorkload && template?.Metadata?.Annotations is null))
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{current.Kind}/{current.Metadata.Name}' cannot receive a " +
                "targeted runtime patch in its current shape. Retry normal reconciliation first.");
        }
    }

    private static void RequireRuntimePatchOwnership(
        IKubernetesObject<V1ObjectMeta> current,
        string owner)
    {
        string? currentOwner = ReadAnnotation(
            current.Metadata,
            KubernetesMetadata.OwnerAnnotation);
        if (!string.Equals(currentOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{current.Kind}/{current.Metadata.Name}' is owned by " +
                $"'{currentOwner ?? "<none>"}', not '{owner}'. The observer never adopts " +
                "objects; retry normal reconciliation first.");
        }
    }

    private static void RequireCurrentRuntimePlan(
        IKubernetesObject<V1ObjectMeta> current,
        IKubernetesObject<V1ObjectMeta> expected)
    {
        string? expectedPlan = ReadAnnotation(
            expected.Metadata,
            KubernetesMetadata.PlanHashAnnotation);
        string? currentPlan = ReadAnnotation(
            current.Metadata,
            KubernetesMetadata.PlanHashAnnotation);
        if (!string.Equals(currentPlan, expectedPlan, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{current.Kind}/{current.Metadata.Name}' no longer matches " +
                "the compilation registered with the observer. Retry normal reconciliation " +
                "before refreshing its public endpoint contract.");
        }
    }

    private static void RequireCurrentWorkloadRevision(
        IKubernetesObject<V1ObjectMeta> current,
        IKubernetesObject<V1ObjectMeta> registered,
        string requestedRevision)
    {
        string? registeredRevision = ReadAnnotation(
            GetPodTemplate(registered)?.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation);
        string? currentRevision = ReadAnnotation(
            GetPodTemplate(current)?.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation);
        if (!string.Equals(currentRevision, registeredRevision, StringComparison.Ordinal)
            && !string.Equals(currentRevision, requestedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{current.Kind}/{current.Metadata.Name}' no longer matches " +
                "the compilation registered with the observer or its requested runtime update. " +
                "Retry normal reconciliation before refreshing its public endpoint contract.");
        }
    }

    private static IKubernetesObject<V1ObjectMeta> CloneWorkload(
        IKubernetesObject<V1ObjectMeta> workload) => workload switch
        {
            V1Deployment deployment => KubernetesJson.Deserialize<V1Deployment>(
                KubernetesJson.Serialize(deployment)),
            V1StatefulSet statefulSet => KubernetesJson.Deserialize<V1StatefulSet>(
                KubernetesJson.Serialize(statefulSet)),
            V1DaemonSet daemonSet => KubernetesJson.Deserialize<V1DaemonSet>(
                KubernetesJson.Serialize(daemonSet)),
            V1Job job => KubernetesJson.Deserialize<V1Job>(KubernetesJson.Serialize(job)),
            _ => throw new InvalidOperationException(
                $"Unsupported Kubernetes workload kind '{workload.Kind}'."),
        };

    private static long NextRevision(long current) =>
        current == long.MaxValue ? 1 : current + 1;

    private static void PrepareForCreate(V1ObjectMeta metadata)
    {
        metadata.ResourceVersion = null;
        metadata.Uid = null;
        metadata.ManagedFields = null;
        metadata.CreationTimestamp = null;
        metadata.DeletionTimestamp = null;
    }

    public async Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable mutation = await _observations
            .EnterMutationAsync(context, cancellationToken)
            .ConfigureAwait(false);
        _observations.Stop(context);
    }

    internal void BeginSession()
    {
        lock (_reportedWarnings)
        {
            _reportedWarnings.Clear();
        }
    }

    internal async Task PruneRemovedResourcesAsync(
        IApplicationModel model,
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);

        var currentResources = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            IApplicationResource resource = model.Descriptors[index].Resource;
            if (resource is not IExternalResource)
            {
                currentResources.Add(KubernetesMetadata.ResourceName(resource.Name));
            }
        }

        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects = await _resources
            .ListSupportedObjectsAsync(
                namespaceName,
                resourceLabel: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var stale = new List<IKubernetesObject<V1ObjectMeta>>();
        for (int index = 0; index < objects.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> candidate = objects[index];
            string? resource = null;
            _ = candidate.Metadata?.Labels?.TryGetValue(
                KubernetesMetadata.ResourceLabel,
                out resource);
            if (resource is null || currentResources.Contains(resource))
            {
                continue;
            }

            // A removed resource no longer has a controller context. Stop its workloads and
            // remove its ephemeral support objects at application-session start, while retaining
            // every PVC so a rename can never silently repurpose persistent data.
            if (candidate is V1PersistentVolumeClaim)
            {
                continue;
            }

            RequireOwnership(
                candidate,
                candidate,
                model.Owner,
                model.Adopt,
                "remove the object for a resource absent from the current model");
            RequireSafeStatefulSetControllerDeletion(candidate, objects);
            stale.Add(candidate);
        }

        stale.Sort(static (left, right) =>
        {
            int order = GetDependencyOrder(left).CompareTo(GetDependencyOrder(right));
            return order != 0
                ? order
                : string.Compare(
                    left.Metadata?.Name,
                    right.Metadata?.Name,
                    StringComparison.Ordinal);
        });
        ExceptionDispatchInfo? firstFailure = null;
        for (int index = stale.Count - 1; index >= 0; index--)
        {
            try
            {
                await _resources.DeleteAsync(stale[index], cancellationToken).ConfigureAwait(false);
                await WaitForDeletionAsync(stale[index], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
    }

    private static int GetDependencyOrder(IKubernetesObject<V1ObjectMeta> resource) =>
        resource switch
        {
            V1ConfigMap => 0,
            V1Secret => 1,
            V1Service => 2,
            V1PersistentVolumeClaim => 3,
            V1Deployment or V1StatefulSet or V1DaemonSet or V1Job => 4,
            _ => throw new InvalidOperationException(
                $"Unsupported Kubernetes object kind '{resource.Kind}'."),
        };

    public async Task DeleteAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable mutation = await _observations
            .EnterMutationAsync(context, cancellationToken)
            .ConfigureAwait(false);

        KubernetesPlanCompilation compilation = Compile(
            context,
            new DeletionContainerImageArtifact(context.Resource.Id),
            CreateDeletionInputs(context.Plan),
            includeObservedEndpoints: false);
        _observations.BeginTeardown(context);
        ExceptionDispatchInfo? firstFailure = null;

        for (int index = compilation.Objects.Count - 1; index >= 0; index--)
        {
            try
            {
                k8s.IKubernetesObject<k8s.Models.V1ObjectMeta> desired = compilation.Objects[index];
                k8s.IKubernetesObject<k8s.Models.V1ObjectMeta>? existing = await _resources
                    .ReadAsync(desired, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    continue;
                }

                RequireOwnership(existing, desired, context, "remove it");
                await _resources.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
                await WaitForDeletionAsync(desired, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _observations.Stop(context);
                throw;
            }
            catch (Exception exception)
            {
                // Teardown is deliberately best-effort: retain the first error while attempting
                // every remaining desired object in reverse dependency order.
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        if (firstFailure is null)
        {
            _observations.Unregister(context);
        }
        else
        {
            _observations.Stop(context);
        }

        firstFailure?.Throw();
    }

    private static void RequireOwnership(
        IKubernetesObject<V1ObjectMeta> existing,
        IKubernetesObject<V1ObjectMeta> desired,
        IResourceControlContext context,
        string action)
    {
        if (existing is V1PersistentVolumeClaim)
        {
            RequirePersistentVolumeClaimIdentity(existing.Metadata, context);
        }

        RequireOwnership(
            existing,
            desired,
            context.Model.Owner,
            context.Model.Adopt,
            action);
    }

    private static void RequireOwnership(
        IKubernetesObject<V1ObjectMeta> existing,
        IKubernetesObject<V1ObjectMeta> desired,
        string owner,
        bool adopt,
        string action)
    {
        string? existingOwner = null;
        _ = existing.Metadata?.Annotations?.TryGetValue(
            KubernetesMetadata.OwnerAnnotation,
            out existingOwner);
        if (!string.Equals(existingOwner, owner, StringComparison.Ordinal)
            && !adopt)
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{desired.Kind}/{desired.Metadata.Name}' is owned by " +
                $"'{existingOwner ?? "<none>"}', not '{owner}'. " +
                $"Pass --adopt to {action}.");
        }
    }

    private static void RequirePersistentVolumeClaimIdentity(
        V1ObjectMeta? metadata,
        IResourceControlContext context)
    {
        string expectedResource = KubernetesMetadata.ResourceName(context.Plan.Resource);
        RequirePersistentVolumeClaimIdentity(
            metadata,
            expectedResource,
            context.Model.Adopt);
    }

    internal static void RequirePersistentVolumeClaimIdentity(
        V1ObjectMeta? metadata,
        string expectedResource,
        bool adopt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedResource);
        string? existingResource = null;
        _ = metadata?.Labels?.TryGetValue(
            KubernetesMetadata.ResourceLabel,
            out existingResource);
        if (existingResource is not null
            && !string.Equals(existingResource, expectedResource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes PersistentVolumeClaim/{metadata?.Name} belongs to Cohesion " +
                $"resource '{existingResource}', not '{expectedResource}'. Refusing to reuse " +
                "persistent data across resource identities, including during --adopt.");
        }

        if (existingResource is null && !adopt)
        {
            throw new InvalidOperationException(
                $"Kubernetes PersistentVolumeClaim/{metadata?.Name} has no " +
                $"'{KubernetesMetadata.ResourceLabel}' identity. Pass --adopt to claim it for " +
                $"resource '{expectedResource}'.");
        }
    }

    private async Task<IKubernetesObject<V1ObjectMeta>?> RequireUnchangedAsync(
        IKubernetesObject<V1ObjectMeta> desired,
        IKubernetesObject<V1ObjectMeta>? expected,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        IKubernetesObject<V1ObjectMeta>? current = await _resources
            .ReadAsync(desired, cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            RequireOwnership(current, desired, context, "take ownership");
        }

        if (expected is null && current is null)
        {
            return null;
        }

        if (expected is null || current is null || !SameObjectVersion(expected, current))
        {
            throw new InvalidOperationException(
                $"Kubernetes object '{desired.Kind}/{desired.Metadata.Name}' changed while its " +
                "ownership was being checked. Retry reconciliation against the latest object.");
        }

        // resourceVersion makes the ownership check and forced server-side apply one optimistic
        // transaction. A concurrent write is rejected instead of being overwritten.
        desired.Metadata.ResourceVersion = current.Metadata?.ResourceVersion;
        return current;
    }

    private async Task CreateOrApplyAsync(
        IKubernetesObject<V1ObjectMeta> desired,
        IKubernetesObject<V1ObjectMeta>? existing,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            await _resources.ApplyAsync(desired, force: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        desired.Metadata.ResourceVersion = null;
        if (await _resources.TryCreateAsync(desired, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        IKubernetesObject<V1ObjectMeta>? raced = await _resources
            .ReadAsync(desired, cancellationToken)
            .ConfigureAwait(false);
        if (raced is not null)
        {
            RequireOwnership(raced, desired, context, "take ownership");
        }

        throw new InvalidOperationException(
            $"Kubernetes object '{desired.Kind}/{desired.Metadata.Name}' was created while its " +
            "absence was being checked. Retry reconciliation against the latest object.");
    }

    private static bool SameObjectVersion(
        IKubernetesObject<V1ObjectMeta> expected,
        IKubernetesObject<V1ObjectMeta> current)
    {
        string? expectedUid = expected.Metadata?.Uid;
        string? currentUid = current.Metadata?.Uid;
        if (expectedUid is not null
            && !string.Equals(expectedUid, currentUid, StringComparison.Ordinal))
        {
            return false;
        }

        string? expectedVersion = expected.Metadata?.ResourceVersion;
        return expectedVersion is null
            || string.Equals(expectedVersion, current.Metadata?.ResourceVersion, StringComparison.Ordinal);
    }

    private void StampRuntimeInputRevision(
        KubernetesPlanCompilation compilation,
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>?> existing)
    {
        (V1ConfigMap desiredConfigMap, int configMapIndex) = FindObject<V1ConfigMap>(compilation);
        (V1Secret desiredSecret, int secretIndex) = FindObject<V1Secret>(compilation);
        (IKubernetesObject<V1ObjectMeta> desiredWorkload, int workloadIndex) =
            FindWorkload(compilation);
        V1ConfigMap? existingConfigMap = existing[configMapIndex] as V1ConfigMap;
        V1Secret? existingSecret = existing[secretIndex] as V1Secret;
        IKubernetesObject<V1ObjectMeta>? existingWorkload = existing[workloadIndex];

        bool changed = !ConfigMapContentsEqual(desiredConfigMap, existingConfigMap)
            || !SecretContentsEqual(desiredSecret, existingSecret);
        long current = Math.Max(
            ReadRevision(existingConfigMap?.Metadata),
            Math.Max(
                ReadRevision(existingSecret?.Metadata),
                ReadRevision(GetPodTemplate(existingWorkload)?.Metadata)));
        long revision = current == 0 || changed
            ? current == long.MaxValue ? 1 : current + 1
            : current;
        string value = revision.ToString(CultureInfo.InvariantCulture);

        SetAnnotation(
            desiredConfigMap.Metadata,
            KubernetesMetadata.RuntimeInputRevisionAnnotation,
            value);
        SetAnnotation(
            desiredSecret.Metadata,
            KubernetesMetadata.RuntimeInputRevisionAnnotation,
            value);
        SetAnnotation(
            GetRequiredPodTemplate(desiredWorkload).Metadata,
            KubernetesMetadata.RuntimeInputRevisionAnnotation,
            value);
    }

    private static bool StampWorkloadRevisions(
        KubernetesPlanCompilation compilation,
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>?> existing,
        string owner)
    {
        (IKubernetesObject<V1ObjectMeta> desiredWorkload, int workloadIndex) =
            FindWorkload(compilation);
        IKubernetesObject<V1ObjectMeta>? existingWorkload = existing[workloadIndex];
        ValidateImmutableWorkloadFields(desiredWorkload, existingWorkload);
        V1PodTemplateSpec template = GetRequiredPodTemplate(desiredWorkload);
        template.Metadata.Annotations?.Remove(KubernetesMetadata.WorkloadRevisionAnnotation);
        string workloadRevision = ComputeWorkloadRevision(desiredWorkload.Kind, template);
        SetAnnotation(
            template.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation,
            workloadRevision);
        SetAnnotation(
            desiredWorkload.Metadata,
            KubernetesMetadata.WorkloadRevisionAnnotation,
            workloadRevision);
        if (desiredWorkload is V1Job desiredJob)
        {
            SetAnnotation(
                desiredJob.Metadata,
                KubernetesMetadata.JobSpecRevisionAnnotation,
                ComputeRevision(desiredJob.Spec));
            return false;
        }

        if (desiredWorkload is not V1StatefulSet desiredStatefulSet)
        {
            return false;
        }

        string desiredRevision = ComputeClaimRevision(
            desiredStatefulSet.Spec.VolumeClaimTemplates);
        SetAnnotation(
            desiredStatefulSet.Metadata,
            KubernetesMetadata.VolumeClaimsRevisionAnnotation,
            desiredRevision);
        if (existing[workloadIndex] is not V1StatefulSet existingStatefulSet)
        {
            return false;
        }

        if (!ClaimTemplatesEquivalent(desiredStatefulSet, existingStatefulSet))
        {
            throw new InvalidOperationException(
                $"Kubernetes StatefulSet '{desiredStatefulSet.Metadata.Name}' has changed " +
                "volumeClaimTemplates, which Kubernetes does not permit in place. " +
                "Resize its existing claims separately or teardown and recreate the application.");
        }

        string resource = desiredStatefulSet.Metadata.Labels[
            KubernetesMetadata.ResourceLabel];
        if (!ClaimTemplateIdentitiesMatch(
                existingStatefulSet.Spec.VolumeClaimTemplates,
                owner,
                resource))
        {
            RequireSafeStatefulSetControllerDeletion(existingStatefulSet);

            // Claim-template metadata is immutable. Recreate only the controller during an
            // adoption so retained claims keep their data and future ordinal claims inherit the
            // new owner identity from the replacement template. Preserve server-defaulted claim
            // fields so future ordinals retain the original storage behavior.
            desiredStatefulSet.Spec.VolumeClaimTemplates = CopyClaimTemplatesForAdoption(
                desiredStatefulSet.Spec.VolumeClaimTemplates,
                existingStatefulSet.Spec.VolumeClaimTemplates,
                owner);
            return true;
        }

        // volumeClaimTemplates is immutable as a whole. Preserve the API server's stored value
        // (including defaults and the creation-time plan annotation) while updating the mutable
        // pod template and controller fields.
        desiredStatefulSet.Spec.VolumeClaimTemplates =
            existingStatefulSet.Spec.VolumeClaimTemplates;
        return false;
    }

    private static IList<V1PersistentVolumeClaim> CopyClaimTemplatesForAdoption(
        IList<V1PersistentVolumeClaim> desiredClaims,
        IList<V1PersistentVolumeClaim> existingClaims,
        string owner)
    {
        var copies = new List<V1PersistentVolumeClaim>(existingClaims.Count);
        for (int index = 0; index < existingClaims.Count; index++)
        {
            V1PersistentVolumeClaim desired = desiredClaims[index];
            V1PersistentVolumeClaim copy = KubernetesJson.Deserialize<V1PersistentVolumeClaim>(
                KubernetesJson.Serialize(existingClaims[index]));
            MergeMetadata(copy.Metadata, desired.Metadata);
            KubernetesMetadata.RestoreRequiredMetadata(
                copy.Metadata,
                desired.Metadata.Name,
                desired.Metadata.NamespaceProperty ?? string.Empty,
                desired.Metadata.Labels[KubernetesMetadata.ResourceLabel],
                desired.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation],
                owner);
            copy.Metadata.NamespaceProperty = null;
            copy.Metadata.ResourceVersion = null;
            copy.Metadata.Uid = null;
            copy.Metadata.ManagedFields = null;
            copy.Metadata.CreationTimestamp = null;
            copy.Metadata.DeletionTimestamp = null;
            copies.Add(copy);
        }

        return copies;
    }

    private static void MergeMetadata(V1ObjectMeta target, V1ObjectMeta source)
    {
        target.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        target.Annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.Labels is not null)
        {
            foreach ((string key, string value) in source.Labels)
            {
                target.Labels[key] = value;
            }
        }

        if (source.Annotations is not null)
        {
            foreach ((string key, string value) in source.Annotations)
            {
                target.Annotations[key] = value;
            }
        }
    }

    private static bool ClaimTemplateIdentitiesMatch(
        IList<V1PersistentVolumeClaim> claims,
        string owner,
        string resource)
    {
        for (int index = 0; index < claims.Count; index++)
        {
            if (!string.Equals(
                    ReadAnnotation(claims[index].Metadata, KubernetesMetadata.OwnerAnnotation),
                    owner,
                    StringComparison.Ordinal)
                || claims[index].Metadata?.Labels is null
                || !claims[index].Metadata.Labels.TryGetValue(
                    KubernetesMetadata.ResourceLabel,
                    out string? claimResource)
                || !string.Equals(claimResource, resource, StringComparison.Ordinal)
                || !claims[index].Metadata.Labels.TryGetValue(
                    KubernetesMetadata.ManagedByLabel,
                    out string? managedBy)
                || !string.Equals(
                    managedBy,
                    KubernetesMetadata.ManagedByValue,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateImmutableWorkloadFields(
        IKubernetesObject<V1ObjectMeta> desired,
        IKubernetesObject<V1ObjectMeta>? existing)
    {
        string? changedField = (desired, existing) switch
        {
            (V1Deployment desiredDeployment, V1Deployment existingDeployment)
                when !JsonEquivalent(
                    desiredDeployment.Spec?.Selector,
                    existingDeployment.Spec?.Selector) => "spec.selector",
            (V1DaemonSet desiredDaemonSet, V1DaemonSet existingDaemonSet)
                when !JsonEquivalent(
                    desiredDaemonSet.Spec?.Selector,
                    existingDaemonSet.Spec?.Selector) => "spec.selector",
            (V1StatefulSet desiredStatefulSet, V1StatefulSet existingStatefulSet)
                when !string.Equals(
                    desiredStatefulSet.Spec?.ServiceName,
                    existingStatefulSet.Spec?.ServiceName,
                    StringComparison.Ordinal) => "spec.serviceName",
            (V1StatefulSet desiredStatefulSet, V1StatefulSet existingStatefulSet)
                when !JsonEquivalent(
                    desiredStatefulSet.Spec?.Selector,
                    existingStatefulSet.Spec?.Selector) => "spec.selector",
            (V1StatefulSet desiredStatefulSet, V1StatefulSet existingStatefulSet)
                when !string.Equals(
                    desiredStatefulSet.Spec?.PodManagementPolicy ?? "OrderedReady",
                    existingStatefulSet.Spec?.PodManagementPolicy ?? "OrderedReady",
                    StringComparison.Ordinal) => "spec.podManagementPolicy",
            _ => null,
        };

        if (changedField is not null)
        {
            throw new InvalidOperationException(
                $"Kubernetes {desired.Kind} '{desired.Metadata.Name}' has changed immutable field " +
                $"'{changedField}'. Teardown and recreate the workload to apply that change.");
        }
    }

    private static void RequireSafeStatefulSetControllerDeletion(
        IKubernetesObject<V1ObjectMeta> candidate)
    {
        if (candidate is not V1StatefulSet statefulSet
            || statefulSet.Spec?.VolumeClaimTemplates is not { Count: > 0 })
        {
            return;
        }

        string whenDeleted =
            statefulSet.Spec.PersistentVolumeClaimRetentionPolicy?.WhenDeleted ?? "Retain";
        if (!string.Equals(whenDeleted, "Retain", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes StatefulSet '{statefulSet.Metadata?.Name}' cannot be removed " +
                "safely during reconciliation because " +
                "spec.persistentVolumeClaimRetentionPolicy.whenDeleted is not Retain.");
        }
    }

    private static void RequireSafeStatefulSetControllerDeletion(
        IKubernetesObject<V1ObjectMeta> candidate,
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> namespaceObjects)
    {
        RequireSafeStatefulSetControllerDeletion(candidate);
        if (candidate is not V1StatefulSet statefulSet)
        {
            return;
        }

        string? statefulSetUid = statefulSet.Metadata?.Uid;
        for (int objectIndex = 0; objectIndex < namespaceObjects.Count; objectIndex++)
        {
            if (namespaceObjects[objectIndex] is not V1PersistentVolumeClaim claim
                || claim.Metadata?.OwnerReferences is not { Count: > 0 } references)
            {
                continue;
            }

            for (int referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
            {
                V1OwnerReference reference = references[referenceIndex];
                bool sameOwner = statefulSetUid is not null
                    ? string.Equals(reference.Uid, statefulSetUid, StringComparison.Ordinal)
                    : string.Equals(reference.Kind, V1StatefulSet.KubeKind, StringComparison.Ordinal)
                        && string.Equals(
                            reference.Name,
                            statefulSet.Metadata?.Name,
                            StringComparison.Ordinal);
                if (sameOwner)
                {
                    throw new InvalidOperationException(
                        $"Kubernetes StatefulSet '{statefulSet.Metadata?.Name}' cannot be removed " +
                        $"safely because PersistentVolumeClaim/{claim.Metadata.Name} still has " +
                        "an ownerReference to that controller. Remove the ownerReference with an " +
                        "API-server resourceVersion precondition before retrying reconciliation.");
                }
            }
        }
    }

    private async Task WaitForDeletionAsync(
        IKubernetesObject<V1ObjectMeta> desired,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ReadinessBudget);
        try
        {
            while (await _resources
                .ReadAsync(desired, timeout.Token)
                .ConfigureAwait(false) is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Kubernetes {desired.Kind} '{desired.Metadata.Name}' to be deleted " +
                "before continuing reconciliation.");
        }
    }

    private static bool RequiresJobReplacement(V1Job desired, V1Job existing) =>
        existing.Metadata?.DeletionTimestamp is not null
        || !string.Equals(
            ReadAnnotation(desired.Metadata, KubernetesMetadata.JobSpecRevisionAnnotation),
            ReadAnnotation(existing.Metadata, KubernetesMetadata.JobSpecRevisionAnnotation),
            StringComparison.Ordinal);

    private static (T Resource, int Index) FindObject<T>(
        KubernetesPlanCompilation compilation)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            if (compilation.Objects[index] is T resource)
            {
                return (resource, index);
            }
        }

        throw new InvalidOperationException(
            $"The Kubernetes compilation did not contain a {typeof(T).Name}.");
    }

    private static (IKubernetesObject<V1ObjectMeta> Resource, int Index) FindWorkload(
        KubernetesPlanCompilation compilation)
    {
        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> resource = compilation.Objects[index];
            if (resource is V1Deployment or V1StatefulSet or V1DaemonSet or V1Job)
            {
                return (resource, index);
            }
        }

        throw new InvalidOperationException(
            "The Kubernetes compilation did not contain a supported workload.");
    }

    private static V1PodTemplateSpec GetRequiredPodTemplate(
        IKubernetesObject<V1ObjectMeta> workload) =>
        GetPodTemplate(workload) ?? throw new InvalidOperationException(
            $"Kubernetes workload '{workload.Kind}/{workload.Metadata.Name}' has no pod template.");

    private static V1PodTemplateSpec? GetPodTemplate(
        IKubernetesObject<V1ObjectMeta>? workload) => workload switch
        {
            V1Deployment deployment => deployment.Spec?.Template,
            V1StatefulSet statefulSet => statefulSet.Spec?.Template,
            V1DaemonSet daemonSet => daemonSet.Spec?.Template,
            V1Job job => job.Spec?.Template,
            _ => null,
        };

    private static void SetAnnotation(
        V1ObjectMeta metadata,
        string name,
        string value)
    {
        metadata.Annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
        metadata.Annotations[name] = value;
    }

    private static long ReadRevision(V1ObjectMeta? metadata) =>
        long.TryParse(
            ReadAnnotation(metadata, KubernetesMetadata.RuntimeInputRevisionAnnotation),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long value)
        && value > 0
            ? value
            : 0;

    private static string? ReadAnnotation(V1ObjectMeta? metadata, string name)
    {
        string? value = null;
        _ = metadata?.Annotations?.TryGetValue(name, out value);
        return value;
    }

    private static bool ConfigMapContentsEqual(V1ConfigMap desired, V1ConfigMap? existing) =>
        existing is not null
        && StringDictionaryEqual(desired.Data, existing.Data)
        && ByteDictionaryEqual(desired.BinaryData, existing.BinaryData, excludedKey: null);

    private static bool SecretContentsEqual(V1Secret desired, V1Secret? existing) =>
        existing is not null
        && ByteDictionaryEqual(
            desired.Data,
            existing.Data,
            excludedKey: "bootstrap-token");

    private static bool StringDictionaryEqual(
        IDictionary<string, string>? left,
        IDictionary<string, string>? right)
    {
        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        foreach ((string key, string value) in left)
        {
            if (right is null
                || !right.TryGetValue(key, out string? other)
                || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ByteDictionaryEqual(
        IDictionary<string, byte[]>? left,
        IDictionary<string, byte[]>? right,
        string? excludedKey)
    {
        int leftCount = CountIncluded(left, excludedKey);
        int rightCount = CountIncluded(right, excludedKey);
        if (leftCount != rightCount)
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        foreach ((string key, byte[] value) in left)
        {
            if (string.Equals(key, excludedKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (right is null
                || !right.TryGetValue(key, out byte[]? other)
                || value is null
                || other is null
                || !value.AsSpan().SequenceEqual(other))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountIncluded<T>(
        IDictionary<string, T>? values,
        string? excludedKey)
    {
        if (values is null)
        {
            return 0;
        }

        return excludedKey is not null && values.ContainsKey(excludedKey)
            ? values.Count - 1
            : values.Count;
    }

    private static string ComputeRevision<T>(T value)
    {
        string json = KubernetesJson.Serialize(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string ComputeWorkloadRevision(
        string? kind,
        V1PodTemplateSpec template)
    {
        string payload = $"{kind ?? "<unknown>"}\n{KubernetesJson.Serialize(template)}";
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string ComputeClaimRevision(IList<V1PersistentVolumeClaim> claims)
    {
        var normalized = new StringBuilder();
        for (int index = 0; index < claims.Count; index++)
        {
            V1PersistentVolumeClaim claim = claims[index];
            normalized.Append(claim.Metadata?.Name).Append('\n');
            AppendRevisionValues(normalized, claim.Metadata?.Labels, excludedKey: null);
            AppendRevisionValues(
                normalized,
                claim.Metadata?.Annotations,
                KubernetesMetadata.PlanHashAnnotation,
                KubernetesMetadata.OwnerAnnotation);
            normalized.Append(KubernetesJson.Serialize(claim.Spec)).Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString())));
    }

    private static void AppendRevisionValues(
        StringBuilder destination,
        IDictionary<string, string>? values,
        string? excludedKey,
        string? secondExcludedKey = null)
    {
        if (values is null)
        {
            return;
        }

        var keys = new List<string>(values.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (int index = 0; index < keys.Count; index++)
        {
            string key = keys[index];
            if (string.Equals(key, excludedKey, StringComparison.Ordinal)
                || string.Equals(key, secondExcludedKey, StringComparison.Ordinal))
            {
                continue;
            }

            destination.Append(key).Append('=').Append(values[key]).Append('\n');
        }
    }

    private static bool ClaimTemplatesEquivalent(
        V1StatefulSet desired,
        V1StatefulSet existing)
    {
        IList<V1PersistentVolumeClaim> desiredClaims =
            desired.Spec?.VolumeClaimTemplates ?? [];
        IList<V1PersistentVolumeClaim> existingClaims =
            existing.Spec?.VolumeClaimTemplates ?? [];
        if (desiredClaims.Count != existingClaims.Count)
        {
            return false;
        }

        for (int index = 0; index < desiredClaims.Count; index++)
        {
            V1PersistentVolumeClaim desiredClaim = desiredClaims[index];
            V1PersistentVolumeClaim existingClaim = existingClaims[index];
            V1PersistentVolumeClaimSpec? desiredSpec = desiredClaim.Spec;
            V1PersistentVolumeClaimSpec? existingSpec = existingClaim.Spec;
            if (!string.Equals(
                    desiredClaim.Metadata?.Name,
                    existingClaim.Metadata?.Name,
                    StringComparison.Ordinal)
                || !ContainsDesiredMetadata(
                    desiredClaim.Metadata,
                    existingClaim.Metadata)
                || !string.Equals(
                    ReadStorageQuantity(desiredClaim),
                    ReadStorageQuantity(existingClaim),
                    StringComparison.Ordinal)
                || !StringListEqual(
                    desiredSpec?.AccessModes,
                    existingSpec?.AccessModes)
                || !QuantityDictionaryEqual(
                    desiredSpec?.Resources?.Limits,
                    existingSpec?.Resources?.Limits)
                || desiredSpec?.StorageClassName is not null
                    && !string.Equals(
                        desiredSpec.StorageClassName,
                        existingSpec?.StorageClassName,
                        StringComparison.Ordinal)
                || desiredSpec?.VolumeMode is not null
                    && !string.Equals(
                        desiredSpec.VolumeMode,
                        existingSpec?.VolumeMode,
                        StringComparison.Ordinal)
                || desiredSpec?.VolumeMode is null
                    && existingSpec?.VolumeMode is not null and not "Filesystem"
                || desiredSpec?.VolumeAttributesClassName is not null
                    && !string.Equals(
                        desiredSpec.VolumeAttributesClassName,
                        existingSpec?.VolumeAttributesClassName,
                        StringComparison.Ordinal)
                || !string.Equals(
                    desiredSpec?.VolumeName,
                    existingSpec?.VolumeName,
                    StringComparison.Ordinal)
                || !JsonEquivalent(desiredSpec?.Selector, existingSpec?.Selector)
                || !JsonEquivalent(desiredSpec?.DataSource, existingSpec?.DataSource)
                || !JsonEquivalent(desiredSpec?.DataSourceRef, existingSpec?.DataSourceRef))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClaimCompatibleWithTemplate(
        V1PersistentVolumeClaim template,
        V1PersistentVolumeClaim claim)
    {
        V1PersistentVolumeClaimSpec? desired = template.Spec;
        V1PersistentVolumeClaimSpec? actual = claim.Spec;
        return QuantityAtLeast(
                ReadStorageQuantity(claim),
                ReadStorageQuantity(template))
            && StringListEqual(desired?.AccessModes, actual?.AccessModes)
            && QuantityDictionaryEqual(
                desired?.Resources?.Limits,
                actual?.Resources?.Limits)
            && (desired?.StorageClassName is null
                || string.Equals(
                    desired.StorageClassName,
                    actual?.StorageClassName,
                    StringComparison.Ordinal))
            && (desired?.VolumeMode is null
                ? actual?.VolumeMode is null or "Filesystem"
                : string.Equals(
                    desired.VolumeMode,
                    actual?.VolumeMode,
                    StringComparison.Ordinal))
            && (desired?.VolumeAttributesClassName is null
                || string.Equals(
                    desired.VolumeAttributesClassName,
                    actual?.VolumeAttributesClassName,
                    StringComparison.Ordinal))
            && JsonEquivalent(desired?.Selector, actual?.Selector)
            && JsonEquivalent(desired?.DataSource, actual?.DataSource)
            && JsonEquivalent(desired?.DataSourceRef, actual?.DataSourceRef);
    }

    private static bool QuantityAtLeast(string? actual, string? desired)
    {
        if (string.Equals(actual, desired, StringComparison.Ordinal))
        {
            return true;
        }

        return actual is not null
            && desired is not null
            && TryParseQuantity(actual, out decimal actualValue)
            && TryParseQuantity(desired, out decimal desiredValue)
            && actualValue >= desiredValue;
    }

    private static bool TryParseQuantity(string value, out decimal result)
    {
        string number = value;
        decimal multiplier = 1;
        _ = TryRemoveSuffix(value, "Ki", 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "Mi", 1024m * 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "Gi", 1024m * 1024m * 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "Ti", 1024m * 1024m * 1024m * 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "Pi", 1024m * 1024m * 1024m * 1024m * 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "Ei", 1024m * 1024m * 1024m * 1024m * 1024m * 1024m, out number, out multiplier)
            || TryRemoveSuffix(value, "m", 0.001m, out number, out multiplier)
            || TryRemoveSuffix(value, "k", 1_000m, out number, out multiplier)
            || TryRemoveSuffix(value, "M", 1_000_000m, out number, out multiplier)
            || TryRemoveSuffix(value, "G", 1_000_000_000m, out number, out multiplier)
            || TryRemoveSuffix(value, "T", 1_000_000_000_000m, out number, out multiplier)
            || TryRemoveSuffix(value, "P", 1_000_000_000_000_000m, out number, out multiplier)
            || TryRemoveSuffix(value, "E", 1_000_000_000_000_000_000m, out number, out multiplier);

        if (!decimal.TryParse(
                number,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed)
            || parsed < 0)
        {
            result = 0;
            return false;
        }

        try
        {
            result = parsed * multiplier;
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static bool TryRemoveSuffix(
        string value,
        string suffix,
        decimal suffixMultiplier,
        out string number,
        out decimal multiplier)
    {
        if (value.EndsWith(suffix, StringComparison.Ordinal))
        {
            number = value[..^suffix.Length];
            multiplier = suffixMultiplier;
            return true;
        }

        number = value;
        multiplier = 1;
        return false;
    }

    private static string? ReadStorageQuantity(V1PersistentVolumeClaim claim)
    {
        if (claim.Spec?.Resources?.Requests is null
            || !claim.Spec.Resources.Requests.TryGetValue(
                "storage",
                out ResourceQuantity? quantity))
        {
            return null;
        }

        return quantity.ToString();
    }

    private static bool ContainsDesiredMetadata(
        V1ObjectMeta? desired,
        V1ObjectMeta? existing) =>
        ContainsDesiredValues(
            desired?.Labels,
            existing?.Labels,
            KubernetesMetadata.ManagedByLabel,
            KubernetesMetadata.ResourceLabel)
        && ContainsDesiredAnnotations(desired?.Annotations, existing?.Annotations);

    private static bool ContainsDesiredAnnotations(
        IDictionary<string, string>? desired,
        IDictionary<string, string>? existing)
    {
        if (desired is null)
        {
            return true;
        }

        foreach ((string key, string value) in desired)
        {
            if (key is KubernetesMetadata.PlanHashAnnotation
                or KubernetesMetadata.OwnerAnnotation)
            {
                continue;
            }

            if (existing is null
                || !existing.TryGetValue(key, out string? existingValue)
                || !string.Equals(value, existingValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsDesiredValues(
        IDictionary<string, string>? desired,
        IDictionary<string, string>? existing,
        string? excludedKey,
        string? secondExcludedKey)
    {
        if (desired is null)
        {
            return true;
        }

        foreach ((string key, string value) in desired)
        {
            if (string.Equals(key, excludedKey, StringComparison.Ordinal)
                || string.Equals(key, secondExcludedKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (existing is null
                || !existing.TryGetValue(key, out string? existingValue)
                || !string.Equals(value, existingValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool QuantityDictionaryEqual(
        IDictionary<string, ResourceQuantity>? left,
        IDictionary<string, ResourceQuantity>? right)
    {
        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        foreach ((string key, ResourceQuantity value) in left)
        {
            if (right is null
                || !right.TryGetValue(key, out ResourceQuantity? existingValue)
                || !string.Equals(
                    value.ToString(),
                    existingValue.ToString(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool JsonEquivalent<T>(T? left, T? right)
        where T : class
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            KubernetesJson.Serialize(left),
            KubernetesJson.Serialize(right),
            StringComparison.Ordinal);
    }

    private static bool StringListEqual(IList<string>? left, IList<string>? right)
    {
        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (int index = 0; index < leftCount; index++)
        {
            if (!string.Equals(left![index], right![index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private KubernetesPlanCompilation Compile(
        IResourceControlContext context,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        bool includeObservedEndpoints = true,
        IReadOnlyList<ResourceEndpoint>? ownEndpoints = null) => _compiler.Compile(
            context.Plan,
            artifact,
            inputs,
            context.ObservedDependencies,
            KubernetesMetadata.NamespaceName(context.Model.Name),
            context.Model.Owner,
            _options,
            ownEndpoints ?? (includeObservedEndpoints
                ? context.State.GetObservedEndpoints(context.Resource.Id)
                : Array.Empty<ResourceEndpoint>()));

    private async Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> FindStaleObjectsAsync(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        var desired = new HashSet<KubernetesObjectIdentity>();
        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            desired.Add(KubernetesObjectIdentity.Create(compilation.Objects[index]));
        }

        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> existing = await _resources
            .ListSupportedObjectsAsync(
                compilation.NamespaceName,
                KubernetesMetadata.ResourceName(context.Plan.Resource),
                cancellationToken)
            .ConfigureAwait(false);
        var stale = new List<IKubernetesObject<V1ObjectMeta>>();
        var staleIdentities = new HashSet<KubernetesObjectIdentity>();
        IReadOnlyList<V1PersistentVolumeClaim>? namespaceClaims = null;
        for (int index = 0; index < existing.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> candidate = existing[index];
            if (desired.Contains(KubernetesObjectIdentity.Create(candidate)))
            {
                continue;
            }

            RequireOwnership(candidate, candidate, context, "remove the stale object");
            if (candidate is V1PersistentVolumeClaim)
            {
                // Standalone desired claims were matched above. Preserve every other claim;
                // generated StatefulSet claims are validated through the unfiltered namespace
                // list below, including claims missing Cohesion's list-selector labels.
                continue;
            }

            if (candidate is V1StatefulSet)
            {
                namespaceClaims ??= await _resources
                    .ListPersistentVolumeClaimsAsync(
                        compilation.NamespaceName,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireSafeStatefulSetControllerDeletion(candidate, namespaceClaims);
            }

            stale.Add(candidate);
            staleIdentities.Add(KubernetesObjectIdentity.Create(candidate));
        }

        IReadOnlyList<V1PersistentVolumeClaim> statefulClaims =
            await FindStatefulClaimsAsync(compilation, cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < statefulClaims.Count; index++)
        {
            V1PersistentVolumeClaim candidate = statefulClaims[index];
            KubernetesObjectIdentity identity = KubernetesObjectIdentity.Create(candidate);
            if (!staleIdentities.Add(identity))
            {
                continue;
            }

            RequireStatefulClaimOwnership(candidate, context);
            RequireStatefulClaimCompatibility(candidate, compilation);
            stale.Add(candidate);
        }

        return stale;
    }

    private async Task VerifyStatefulClaimOwnershipAsync(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<V1PersistentVolumeClaim> claims = await FindStatefulClaimsAsync(
            compilation,
            cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < claims.Count; index++)
        {
            RequireStatefulClaimOwnership(claims[index], context);
            RequireStatefulClaimCompatibility(claims[index], compilation);
        }

        await AdoptPersistentVolumeClaimsAsync(
            context,
            compilation,
            claims,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<V1PersistentVolumeClaim>> FindStatefulClaimsAsync(
        KubernetesPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        (IKubernetesObject<V1ObjectMeta> workload, _) = FindWorkload(compilation);
        if (workload is not V1StatefulSet statefulSet)
        {
            return [];
        }

        IReadOnlyList<V1PersistentVolumeClaim> namespaceClaims = await _resources
            .ListPersistentVolumeClaimsAsync(compilation.NamespaceName, cancellationToken)
            .ConfigureAwait(false);
        var matches = new List<V1PersistentVolumeClaim>();
        for (int claimIndex = 0; claimIndex < namespaceClaims.Count; claimIndex++)
        {
            V1PersistentVolumeClaim claim = namespaceClaims[claimIndex];
            for (int templateIndex = 0;
                templateIndex < statefulSet.Spec.VolumeClaimTemplates.Count;
                templateIndex++)
            {
                string prefix =
                    $"{statefulSet.Spec.VolumeClaimTemplates[templateIndex].Metadata.Name}-" +
                    $"{statefulSet.Metadata.Name}";
                if (IsGeneratedClaimName(claim.Metadata?.Name, prefix))
                {
                    matches.Add(claim);
                    break;
                }
            }
        }

        return matches;
    }

    private static bool IsGeneratedClaimName(string? claimName, string prefix)
    {
        if (claimName is null
            || !claimName.StartsWith(prefix, StringComparison.Ordinal)
            || claimName.Length <= prefix.Length + 1
            || claimName[prefix.Length] != '-')
        {
            return false;
        }

        for (int index = prefix.Length + 1; index < claimName.Length; index++)
        {
            if (claimName[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireStatefulClaimOwnership(
        V1PersistentVolumeClaim claim,
        IResourceControlContext context)
    {
        RequireOwnership(claim, claim, context, "adopt the StatefulSet claim");
    }

    private static void RequireStatefulClaimCompatibility(
        V1PersistentVolumeClaim claim,
        KubernetesPlanCompilation compilation)
    {
        (IKubernetesObject<V1ObjectMeta> workload, _) = FindWorkload(compilation);
        if (workload is not V1StatefulSet statefulSet)
        {
            return;
        }

        V1PersistentVolumeClaim? template = null;
        for (int index = 0; index < statefulSet.Spec.VolumeClaimTemplates.Count; index++)
        {
            V1PersistentVolumeClaim candidate = statefulSet.Spec.VolumeClaimTemplates[index];
            string prefix = $"{candidate.Metadata.Name}-{statefulSet.Metadata.Name}";
            if (IsGeneratedClaimName(claim.Metadata?.Name, prefix))
            {
                template = candidate;
                break;
            }
        }

        if (template is null || !ClaimCompatibleWithTemplate(template, claim))
        {
            throw new InvalidOperationException(
                $"Kubernetes PersistentVolumeClaim/{claim.Metadata?.Name} is not compatible " +
                $"with StatefulSet '{statefulSet.Metadata.Name}' claim template. Refusing to " +
                "mount retained persistent data with a different storage specification.");
        }
    }

    private async Task AdoptPersistentVolumeClaimsAsync<TClaim>(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        IReadOnlyList<TClaim> candidates,
        CancellationToken cancellationToken)
        where TClaim : IKubernetesObject<V1ObjectMeta>
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] is not V1PersistentVolumeClaim candidate)
            {
                continue;
            }

            string? candidateOwner = ReadAnnotation(
                candidate.Metadata,
                KubernetesMetadata.OwnerAnnotation);
            string? candidateResource = null;
            _ = candidate.Metadata.Labels?.TryGetValue(
                KubernetesMetadata.ResourceLabel,
                out candidateResource);
            if (string.Equals(candidateOwner, context.Model.Owner, StringComparison.Ordinal)
                && string.Equals(
                    candidateResource,
                    KubernetesMetadata.ResourceName(context.Plan.Resource),
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(candidate.Metadata.ResourceVersion))
            {
                throw new InvalidOperationException(
                    $"Kubernetes PersistentVolumeClaim/{candidate.Metadata.Name} cannot be " +
                    "adopted without an API-server resourceVersion precondition.");
            }

            var metadataPatch = new V1PersistentVolumeClaim
            {
                ApiVersion = V1PersistentVolumeClaim.KubeApiVersion,
                Kind = V1PersistentVolumeClaim.KubeKind,
                Metadata = KubernetesMetadata.CreateObjectMeta(
                    candidate.Metadata.Name,
                    compilation.NamespaceName,
                    KubernetesMetadata.ResourceName(context.Plan.Resource),
                    compilation.PlanHash,
                    context.Model.Owner),
            };
            metadataPatch.Metadata.ResourceVersion = candidate.Metadata.ResourceVersion;
            await _resources.ApplyAsync(metadataPatch, force: true, cancellationToken)
                .ConfigureAwait(false);
            KubernetesMetadata.RestoreRequiredMetadata(
                candidate.Metadata,
                candidate.Metadata.Name,
                compilation.NamespaceName,
                KubernetesMetadata.ResourceName(context.Plan.Resource),
                compilation.PlanHash,
                context.Model.Owner);
        }
    }

    private async Task DeleteStaleObjectsAsync(
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> stale,
        CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? firstFailure = null;
        for (int index = stale.Count - 1; index >= 0; index--)
        {
            IKubernetesObject<V1ObjectMeta> candidate = stale[index];
            try
            {
                // StatefulSet claim-template PVCs inherit Cohesion metadata but are not
                // standalone compiler objects. Preserve their data and, when requested,
                // atomically adopt only the Cohesion metadata fields.
                if (candidate is V1PersistentVolumeClaim)
                {
                    continue;
                }

                await _resources.DeleteAsync(candidate, cancellationToken).ConfigureAwait(false);
                await WaitForDeletionAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Desired state has already been applied. Preserve the first cleanup error while
                // attempting every other stale object so the next reconcile has minimal work.
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
    }

    private void ReportWarnings(IReadOnlyList<string> warnings)
    {
        for (int index = 0; index < warnings.Count; index++)
        {
            string warning = warnings[index];
            lock (_reportedWarnings)
            {
                if (!_reportedWarnings.Add(warning))
                {
                    continue;
                }
            }

            _options.WarningHandler(warning);
        }
    }

    private static ResourceInputs CreateDeletionInputs(ResourcePlan plan)
    {
        var mounts = new Dictionary<string, ResourceMountInput>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Container.Mounts.Count; index++)
        {
            MountBinding binding = plan.Container.Mounts[index];
            mounts[binding.Mount] = ResourceMountInput.Resolved(binding.Source, ReadOnlyMemory<byte>.Empty);
        }

        return new ResourceInputs(mounts, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }

    private sealed class DeletionContainerImageArtifact : IContainerImageArtifact
    {
        public DeletionContainerImageArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }

        public string Repository => "cohesion-delete.invalid/image";

        public string Digest => $"sha256:{new string('0', 64)}";

        public string? Tag => null;
    }

    private readonly record struct KubernetesObjectIdentity(
        string ApiVersion,
        string Kind,
        string Name)
    {
        public static KubernetesObjectIdentity Create(
            k8s.IKubernetesObject<k8s.Models.V1ObjectMeta> resource)
        {
            KubernetesObjectTypes.Restore(resource);
            string apiVersion = resource.ApiVersion
                ?? throw new InvalidOperationException(
                    "A supported Kubernetes object must have apiVersion.");
            string kind = resource.Kind
                ?? throw new InvalidOperationException(
                    "A supported Kubernetes object must have kind.");
            string name = resource.Metadata?.Name
                ?? throw new InvalidOperationException(
                    "A supported Kubernetes object must have metadata.name.");
            return new KubernetesObjectIdentity(apiVersion, kind, name);
        }
    }
}
