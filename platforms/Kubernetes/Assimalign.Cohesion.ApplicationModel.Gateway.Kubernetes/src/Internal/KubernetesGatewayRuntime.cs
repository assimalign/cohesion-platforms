using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesGatewayResourceApi : IKubernetesResourceApi
{
    private readonly Func<IKubernetes> _client;

    public KubernetesGatewayResourceApi(Func<IKubernetes> client) => _client = client;

    private KubernetesResourceApi ForApply(IKubernetesObject<V1ObjectMeta> value) =>
        new(_client(), value.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation]);

    private KubernetesResourceApi ForObservation() =>
        new(_client(), "cohesion-observer");

    public Task<bool> TryCreateAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default) =>
        ForApply(resource).TryCreateAsync(resource, cancellationToken);

    public Task ApplyAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        bool force,
        CancellationToken cancellationToken = default) =>
        ForApply(resource).ApplyAsync(resource, force, cancellationToken);

    public Task JsonPatchAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        string patch,
        CancellationToken cancellationToken = default) =>
        ForApply(resource).JsonPatchAsync(resource, patch, cancellationToken);

    public Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default) =>
        ForObservation().ReadAsync(resource, cancellationToken);

    public Task DeleteAsync(
        IKubernetesObject<V1ObjectMeta> resource,
        CancellationToken cancellationToken = default) =>
        ForObservation().DeleteAsync(resource, cancellationToken);

    public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
        string namespaceName,
        string? resourceLabel,
        CancellationToken cancellationToken = default) =>
        ForObservation().ListSupportedObjectsAsync(namespaceName, resourceLabel, cancellationToken);

    public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        string namespaceName,
        CancellationToken cancellationToken = default) =>
        ForObservation().ListPersistentVolumeClaimsAsync(namespaceName, cancellationToken);

    public Task<V1PodList> ListPodsAsync(
        string namespaceName,
        string? labelSelector = null,
        CancellationToken cancellationToken = default) =>
        ForObservation().ListPodsAsync(namespaceName, labelSelector, cancellationToken);

    public Task<V1Endpoints?> ReadEndpointsAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default) =>
        ForObservation().ReadEndpointsAsync(namespaceName, serviceName, cancellationToken);

    public IAsyncEnumerable<V1Pod> WatchPodsAsync(CancellationToken cancellationToken = default) =>
        ForObservation().WatchPodsAsync(cancellationToken);
}

internal sealed class KubernetesGatewayObservationRegistry : IKubernetesObservationRegistry
{
    private static readonly TimeSpan MaximumResyncInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchRetryInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly IKubernetesResourceApi _resources;
    private readonly Action<IApplicationModel> _teardown;
    private readonly Func<IApplicationModel, CancellationToken, Task> _refreshExport;
    private readonly Func<IApplicationModel, bool> _canCommitTeardown;
    private readonly Func<
        IResourceControlContext,
        KubernetesPlanCompilation,
        IReadOnlyList<ResourceEndpoint>,
        CancellationToken,
        Task<KubernetesPlanCompilation>> _refreshRuntime;
    private readonly TimeSpan _resyncInterval;
    private readonly SemaphoreSlim _resync = new(0, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _mutationGates = new(StringComparer.Ordinal);
    private readonly Dictionary<ApplicationName, TeardownOutcome> _teardowns = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _sessionActive;

    public KubernetesGatewayObservationRegistry(
        IKubernetesResourceApi resources,
        Action<IApplicationModel> teardown,
        Func<IApplicationModel, CancellationToken, Task>? refreshExport = null,
        TimeSpan? readinessBudget = null,
        Func<IApplicationModel, bool>? canCommitTeardown = null,
        Func<
            IResourceControlContext,
            KubernetesPlanCompilation,
            IReadOnlyList<ResourceEndpoint>,
            CancellationToken,
            Task<KubernetesPlanCompilation>>? refreshRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(teardown);
        _resources = resources;
        _teardown = teardown;
        _refreshExport = refreshExport ?? NoOpExportRefreshAsync;
        _canCommitTeardown = canCommitTeardown ?? (static _ => true);
        _refreshRuntime = refreshRuntime ?? NoOpRuntimeRefreshAsync;
        _resyncInterval = GetResyncInterval(readinessBudget ?? TimeSpan.FromSeconds(60));
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_sessionActive)
            {
                throw new InvalidOperationException("The Kubernetes informer is already running.");
            }

            _sessionActive = true;
            _stop = new CancellationTokenSource();
            _loop = ObserveSessionAsync(_stop.Token);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        TeardownOutcome[] completedTeardowns;
        lock (_sync)
        {
            if (!_sessionActive)
            {
                return;
            }

            _stop?.Cancel();
            loop = _loop;
            _loop = null;
            stop = _stop;
            _stop = null;
            foreach (Entry entry in _entries.Values)
            {
                entry.Active = false;
            }

            _entries.Clear();
            var completed = new List<TeardownOutcome>(_teardowns.Count);
            foreach (TeardownOutcome outcome in _teardowns.Values)
            {
                if (outcome.CanCommit)
                {
                    completed.Add(outcome);
                }
            }

            completedTeardowns = [.. completed];
        }

        ExceptionDispatchInfo? failure = null;
        try
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            stop?.Dispose();
        }

        for (int index = 0; index < completedTeardowns.Length; index++)
        {
            try
            {
                TeardownOutcome outcome = completedTeardowns[index];
                _teardown(outcome.Model);
                lock (_sync)
                {
                    if (_teardowns.TryGetValue(outcome.Model.Name, out TeardownOutcome? current)
                        && ReferenceEquals(current, outcome))
                    {
                        _teardowns.Remove(outcome.Model.Name);
                    }
                }
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        lock (_sync)
        {
            // An unsuccessful controller deletion is already surfaced by ApplicationGateway.
            // Close this observer session while leaving its namespace uncommitted, so startup
            // rollback and a later retry never inherit a cancelled informer loop.
            _teardowns.Clear();
            _sessionActive = false;
        }

        failure?.Throw();
    }

    public void Register(IResourceControlContext context, KubernetesPlanCompilation compilation)
    {
        IKubernetesObject<V1ObjectMeta> workload = compilation.Objects[^1];
        string key = Key(context);
        lock (_sync)
        {
            var entry = new Entry(
                key,
                context,
                compilation,
                workload,
                GetMutationGateLocked(key));
            if (_entries.TryGetValue(key, out Entry? previous))
            {
                previous.Active = false;
                entry.CopyObservationFrom(previous);
            }

            _entries[key] = entry;
            if (_teardowns.TryGetValue(context.Model.Name, out TeardownOutcome? teardown))
            {
                // A newly desired generation invalidates an earlier successful deletion for the
                // same logical resource. A later explicit BeginTeardown call can retry it.
                teardown.Expected.Add(key);
                teardown.Succeeded.Remove(key);
                teardown.Failed.Add(key);
            }

            if (previous is null)
            {
                context.State.SetState(
                    context.Resource.Id,
                    ResourceLifecycle.Starting,
                    observedEndpoints: compilation.Endpoints);
            }
        }

        SignalResync();
    }

    public async Task<IDisposable> EnterMutationAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        SemaphoreSlim gate;
        lock (_sync)
        {
            gate = GetMutationGateLocked(Key(context));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MutationLease(gate);
    }

    public void BeginTeardown(IResourceControlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_sync)
        {
            // Teardown accounting belongs only to the active informer session. A failed session
            // is closed by StopAsync; a retry starts with a fresh list/watch and outcome set.
            if (!_sessionActive)
            {
                return;
            }

            string key = Key(context);
            TeardownOutcome outcome = GetTeardownOutcomeLocked(context.Model);
            if (outcome.Expected.Count == 0)
            {
                // A normal uninstall sees every reconciled entry. Startup rollback sees only
                // the successfully reached prefix, plus the currently failing reconcile whose
                // controller was selected before it could register with the informer.
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Context.Model.Name == context.Model.Name)
                    {
                        outcome.Expected.Add(entry.Key);
                    }
                }
            }

            outcome.Expected.Add(key);
            outcome.Succeeded.Remove(key);
            outcome.Failed.Remove(key);
        }
    }

    public void Stop(IResourceControlContext context)
    {
        lock (_sync)
        {
            RemoveLocked(context);
            TrackTeardownFailureLocked(context);
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        }
    }

    public void Unregister(IResourceControlContext context)
    {
        lock (_sync)
        {
            RemoveLocked(context);
            TrackTeardownSuccessLocked(context);
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        }
    }

    private void RemoveLocked(IResourceControlContext context)
    {
        if (_entries.Remove(Key(context), out Entry? entry))
        {
            entry.Active = false;
        }
    }

    private void TrackTeardownSuccessLocked(IResourceControlContext context)
    {
        if (!_teardowns.TryGetValue(context.Model.Name, out TeardownOutcome? outcome))
        {
            return;
        }

        string key = Key(context);
        outcome.Succeeded.Add(key);
        outcome.Failed.Remove(key);
    }

    private void TrackTeardownFailureLocked(IResourceControlContext context)
    {
        if (!_teardowns.TryGetValue(context.Model.Name, out TeardownOutcome? outcome))
        {
            return;
        }

        string key = Key(context);
        outcome.Succeeded.Remove(key);
        outcome.Failed.Add(key);
    }

    private TeardownOutcome GetTeardownOutcomeLocked(IApplicationModel model)
    {
        if (!_teardowns.TryGetValue(model.Name, out TeardownOutcome? outcome))
        {
            outcome = new TeardownOutcome(model, _canCommitTeardown(model));
            _teardowns.Add(model.Name, outcome);
        }

        return outcome;
    }

    private async Task ObserveSessionAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        Task resync = ResyncAsync(cancellationToken);
        Task watch = WatchAsync(cancellationToken);
        await Task.WhenAll(resync, watch).ConfigureAwait(false);
    }

    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Entry[] entries;
            lock (_sync)
            {
                entries = [.. _entries.Values];
            }

            var observations = new Task[entries.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                observations[index] = ObserveEntryAsync(entries[index], cancellationToken);
            }

            await Task.WhenAll(observations).ConfigureAwait(false);

            await _resync
                .WaitAsync(_resyncInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (V1Pod pod in _resources
                    .WatchPodsAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    Entry? entry = FindEntry(pod);
                    if (entry is not null)
                    {
                        await ObserveEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(WatchRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Entry? FindEntry(V1Pod pod)
    {
        string? resource = null;
        _ = pod.Metadata?.Labels?.TryGetValue(KubernetesMetadata.ResourceLabel, out resource);
        if (resource is null || pod.Metadata?.NamespaceProperty is not string namespaceName)
        {
            return null;
        }

        lock (_sync)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (string.Equals(entry.Compilation.NamespaceName, namespaceName, StringComparison.Ordinal)
                    && string.Equals(entry.ResourceLabel, resource, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private async Task ObserveEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(entry))
            {
                return;
            }

            Task<IKubernetesObject<V1ObjectMeta>?> workloadRead = _resources
                .ReadAsync(entry.Workload, cancellationToken);
            Task<V1PodList> podRead = _resources.ListPodsAsync(
                entry.Compilation.NamespaceName,
                $"{KubernetesMetadata.ResourceLabel}={entry.ResourceLabel}",
                cancellationToken);
            Task<ServiceObservation> serviceRead = ObserveServicesAsync(
                entry,
                cancellationToken);
            await Task.WhenAll(workloadRead, podRead, serviceRead).ConfigureAwait(false);

            IKubernetesObject<V1ObjectMeta>? workload = await workloadRead.ConfigureAwait(false);
            if (workload is null)
            {
                TrySetState(
                    entry,
                    entry.WasReady ? ResourceLifecycle.Degraded : ResourceLifecycle.Starting,
                    $"Kubernetes workload '{entry.Workload.Kind}/{entry.Workload.Metadata.Name}' was not found.",
                    entry.ObservedEndpoints);
                return;
            }

            V1PodList pods = await podRead.ConfigureAwait(false);
            ServiceObservation services = await serviceRead.ConfigureAwait(false);
            IReadOnlyList<V1Pod> currentPods = FilterPods(
                pods.Items,
                entry.WorkloadRevision);
            RestartSnapshot restarts = entry.CaptureRestarts(currentPods);
            KubernetesReadinessObservation observation = entry.Compilation.Readiness.Evaluate(
                workload,
                currentPods,
                entry.WasReady,
                services.Ready,
                restarts.ComparisonBaseline);
            IReadOnlyList<ResourceEndpoint> endpoints = services.Endpoints;
            bool endpointsChanged = !EndpointsEqual(entry.ExportedEndpoints, endpoints);
            if (endpointsChanged && IsCurrent(entry))
            {
                KubernetesPlanCompilation refreshed = await _refreshRuntime(
                    entry.Context,
                    entry.Compilation,
                    endpoints,
                    cancellationToken).ConfigureAwait(false);
                if (!TryUpdateCompilation(entry, refreshed, out bool revisionChanged))
                {
                    return;
                }

                if (revisionChanged)
                {
                    if (!TrySetRuntimeRollout(entry, endpoints))
                    {
                        return;
                    }

                    await _refreshExport(entry.Context.Model, cancellationToken).ConfigureAwait(false);
                    TrySetExportedEndpoints(entry, endpoints);
                    return;
                }
            }

            if (!TrySetObservation(
                entry,
                observation,
                endpoints,
                restarts))
            {
                return;
            }

            if (endpointsChanged && IsCurrent(entry))
            {
                await _refreshExport(entry.Context.Model, cancellationToken).ConfigureAwait(false);
                TrySetExportedEndpoints(entry, endpoints);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            TrySetState(
                entry,
                entry.WasReady ? ResourceLifecycle.Degraded : ResourceLifecycle.Starting,
                $"Kubernetes observation failed: {exception.Message}",
                entry.ObservedEndpoints);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<ServiceObservation> ObserveServicesAsync(
        Entry entry,
        CancellationToken cancellationToken)
    {
        var endpoints = new List<ResourceEndpoint>(entry.Compilation.Endpoints);
        bool ready = true;
        for (int index = 0; index < entry.Compilation.Objects.Count; index++)
        {
            if (entry.Compilation.Objects[index] is not V1Service service)
            {
                continue;
            }

            string serviceName = service.Metadata?.Name
                ?? throw new InvalidOperationException(
                    "A compiled Kubernetes Service must have metadata.name.");
            V1Endpoints? serviceEndpoints = await _resources
                .ReadEndpointsAsync(
                    entry.Compilation.NamespaceName,
                    serviceName,
                    cancellationToken)
                .ConfigureAwait(false);
            ready &= HasReadyAddresses(serviceEndpoints);

            if (!string.Equals(service.Spec?.Type, "LoadBalancer", StringComparison.Ordinal))
            {
                continue;
            }

            V1Service? observed = await _resources
                .ReadAsync(service, cancellationToken)
                .ConfigureAwait(false) as V1Service;
            IList<V1LoadBalancerIngress>? ingress = observed?.Status?.LoadBalancer?.Ingress;
            string? host = ingress is { Count: > 0 }
                ? ingress[0].Hostname ?? ingress[0].Ip
                : null;
            if (string.IsNullOrWhiteSpace(host) || service.Spec?.Ports is null)
            {
                continue;
            }

            for (int portIndex = 0; portIndex < service.Spec.Ports.Count; portIndex++)
            {
                V1ServicePort port = service.Spec.Ports[portIndex];
                ResourceEndpoint? internalEndpoint = FindEndpoint(endpoints, port.Name);
                if (internalEndpoint is ResourceEndpoint endpoint)
                {
                    endpoints.Add(endpoint with
                    {
                        Port = port.Port,
                        IsPublic = true,
                        Host = host,
                    });
                }
            }
        }

        return new ServiceObservation(endpoints, ready);
    }

    private static bool HasReadyAddresses(V1Endpoints? endpoints)
    {
        IList<V1EndpointSubset>? subsets = endpoints?.Subsets;
        if (subsets is null)
        {
            return false;
        }

        for (int subsetIndex = 0; subsetIndex < subsets.Count; subsetIndex++)
        {
            if (subsets[subsetIndex].Addresses is { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    private static ResourceEndpoint? FindEndpoint(
        IReadOnlyList<ResourceEndpoint> endpoints,
        string name)
    {
        for (int index = 0; index < endpoints.Count; index++)
        {
            if (!endpoints[index].IsPublic
                && string.Equals(endpoints[index].Name, name, StringComparison.Ordinal))
            {
                return endpoints[index];
            }
        }

        return null;
    }

    private bool IsCurrent(Entry entry)
    {
        lock (_sync)
        {
            return IsCurrentLocked(entry);
        }
    }

    private bool TrySetState(
        Entry entry,
        ResourceLifecycle state,
        string? detail,
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(entry))
            {
                return false;
            }

            entry.Context.State.SetState(
                entry.Context.Resource.Id,
                state,
                detail,
                endpoints);
            return true;
        }
    }

    private bool TrySetObservation(
        Entry entry,
        KubernetesReadinessObservation observation,
        IReadOnlyList<ResourceEndpoint> endpoints,
        RestartSnapshot restarts)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(entry))
            {
                return false;
            }

            entry.Context.State.SetState(
                entry.Context.Resource.Id,
                observation.State,
                observation.Detail,
                endpoints);
            entry.WasReady |= observation.State is ResourceLifecycle.Running;
            entry.CommitRestarts(restarts);
            entry.ObservedEndpoints = endpoints;
            return true;
        }
    }

    private void TrySetExportedEndpoints(
        Entry entry,
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        lock (_sync)
        {
            if (IsCurrentLocked(entry))
            {
                entry.ExportedEndpoints = endpoints;
            }
        }
    }

    private bool TryUpdateCompilation(
        Entry entry,
        KubernetesPlanCompilation compilation,
        out bool revisionChanged)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(entry))
            {
                revisionChanged = false;
                return false;
            }

            revisionChanged = entry.UpdateCompilation(compilation);
            return true;
        }
    }

    private bool TrySetRuntimeRollout(
        Entry entry,
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(entry))
            {
                return false;
            }

            ResourceLifecycle state = entry.WasReady
                ? ResourceLifecycle.Degraded
                : ResourceLifecycle.Starting;
            entry.Context.State.SetState(
                entry.Context.Resource.Id,
                state,
                "Kubernetes public endpoint contract changed; workload rollout is in progress.",
                endpoints);
            entry.ObservedEndpoints = endpoints;
            return true;
        }
    }

    private bool IsCurrentLocked(Entry entry) =>
        entry.Active
        && _entries.TryGetValue(entry.Key, out Entry? current)
        && ReferenceEquals(current, entry);

    private void SignalResync()
    {
        try
        {
            _resync.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending signal already guarantees a prompt full refresh.
        }
    }

    private SemaphoreSlim GetMutationGateLocked(string key)
    {
        if (!_mutationGates.TryGetValue(key, out SemaphoreSlim? gate))
        {
            gate = new SemaphoreSlim(1, 1);
            _mutationGates.Add(key, gate);
        }

        return gate;
    }

    private static bool EndpointsEqual(
        IReadOnlyList<ResourceEndpoint> left,
        IReadOnlyList<ResourceEndpoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<V1Pod> FilterPods(
        IList<V1Pod> pods,
        string? workloadRevision)
    {
        var filtered = new List<V1Pod>(pods.Count);
        for (int index = 0; index < pods.Count; index++)
        {
            if (workloadRevision is null)
            {
                filtered.Add(pods[index]);
                continue;
            }

            string? observedRevision = null;
            _ = pods[index].Metadata?.Annotations?.TryGetValue(
                KubernetesMetadata.WorkloadRevisionAnnotation,
                out observedRevision);
            if (string.Equals(
                    observedRevision,
                    workloadRevision,
                    StringComparison.Ordinal))
            {
                filtered.Add(pods[index]);
            }
        }

        return filtered;
    }

    private static TimeSpan GetResyncInterval(TimeSpan readinessBudget)
    {
        if (readinessBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readinessBudget),
                "Readiness budget must be greater than zero.");
        }

        var interval = TimeSpan.FromTicks(Math.Max(1, readinessBudget.Ticks / 4));
        return interval < MaximumResyncInterval ? interval : MaximumResyncInterval;
    }

    private static Task NoOpExportRefreshAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static Task<KubernetesPlanCompilation> NoOpRuntimeRefreshAsync(
        IResourceControlContext context,
        KubernetesPlanCompilation compilation,
        IReadOnlyList<ResourceEndpoint> endpoints,
        CancellationToken cancellationToken) => Task.FromResult(compilation);

    private static string Key(IResourceControlContext context) =>
        $"{context.Model.Name}/{context.Resource.Name}";

    private readonly record struct ServiceObservation(
        IReadOnlyList<ResourceEndpoint> Endpoints,
        bool Ready);

    private readonly record struct ContainerRestartKey(
        string Pod,
        string Container);

    private readonly record struct RestartSnapshot(
        int ComparisonBaseline,
        IReadOnlyDictionary<ContainerRestartKey, int> Counts);

    private sealed class Entry
    {
        private IReadOnlyDictionary<ContainerRestartKey, int> _restartCounts =
            new Dictionary<ContainerRestartKey, int>();

        public Entry(
            string key,
            IResourceControlContext context,
            KubernetesPlanCompilation compilation,
            IKubernetesObject<V1ObjectMeta> workload,
            SemaphoreSlim gate)
        {
            Key = key;
            Context = context;
            Compilation = compilation;
            Workload = workload;
            Gate = gate;
            if (workload.Metadata?.Labels is null
                || !workload.Metadata.Labels.TryGetValue(
                    KubernetesMetadata.ResourceLabel,
                    out string? resourceLabel)
                || string.IsNullOrWhiteSpace(resourceLabel))
            {
                throw new InvalidOperationException(
                    $"Kubernetes workload '{workload.Kind}/{workload.Metadata?.Name}' is missing " +
                    $"the required '{KubernetesMetadata.ResourceLabel}' label.");
            }

            ResourceLabel = resourceLabel;
            WorkloadRevision = ReadWorkloadRevision(workload);
            ObservedEndpoints = compilation.Endpoints;
            ExportedEndpoints = compilation.Endpoints;
        }

        public string Key { get; }
        public IResourceControlContext Context { get; }
        public KubernetesPlanCompilation Compilation { get; private set; }
        public IKubernetesObject<V1ObjectMeta> Workload { get; private set; }
        public string ResourceLabel { get; }
        public string? WorkloadRevision { get; private set; }
        public SemaphoreSlim Gate { get; }
        public volatile bool Active = true;
        public bool WasReady { get; set; }
        public IReadOnlyList<ResourceEndpoint> ObservedEndpoints { get; set; }
        public IReadOnlyList<ResourceEndpoint> ExportedEndpoints { get; set; }

        public void CopyObservationFrom(Entry previous)
        {
            WasReady = previous.WasReady;
            if (string.Equals(
                WorkloadRevision,
                previous.WorkloadRevision,
                StringComparison.Ordinal))
            {
                _restartCounts = new Dictionary<ContainerRestartKey, int>(
                    previous._restartCounts);
            }

            ObservedEndpoints = previous.ObservedEndpoints;
            ExportedEndpoints = previous.ExportedEndpoints;
        }

        public bool UpdateCompilation(KubernetesPlanCompilation compilation)
        {
            IKubernetesObject<V1ObjectMeta> workload = compilation.Objects[^1];
            string? resourceLabel = null;
            _ = workload.Metadata?.Labels?.TryGetValue(
                KubernetesMetadata.ResourceLabel,
                out resourceLabel);
            if (!string.Equals(resourceLabel, ResourceLabel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refreshed Kubernetes workload '{workload.Kind}/{workload.Metadata?.Name}' " +
                    $"changed resource label '{ResourceLabel}' to '{resourceLabel}'.");
            }

            string? revision = ReadWorkloadRevision(workload);
            bool revisionChanged = !string.Equals(
                revision,
                WorkloadRevision,
                StringComparison.Ordinal);
            if (revisionChanged)
            {
                _restartCounts = new Dictionary<ContainerRestartKey, int>();
            }

            Compilation = compilation;
            Workload = workload;
            WorkloadRevision = revision;
            return revisionChanged;
        }

        public RestartSnapshot CaptureRestarts(IReadOnlyList<V1Pod> pods)
        {
            var current = new Dictionary<ContainerRestartKey, int>();
            int aggregate = 0;
            bool increased = false;
            for (int podIndex = 0; podIndex < pods.Count; podIndex++)
            {
                V1Pod pod = pods[podIndex];
                IList<V1ContainerStatus>? statuses = pod.Status?.ContainerStatuses;
                if (statuses is null)
                {
                    continue;
                }

                string podIdentity = ReadPodIdentity(pod, podIndex);
                for (int statusIndex = 0; statusIndex < statuses.Count; statusIndex++)
                {
                    V1ContainerStatus status = statuses[statusIndex];
                    var key = new ContainerRestartKey(
                        podIdentity,
                        ReadContainerIdentity(status, statusIndex));
                    int count = status.RestartCount;
                    aggregate += count;
                    if (_restartCounts.TryGetValue(key, out int previous))
                    {
                        increased |= count > previous;
                    }
                    else
                    {
                        increased |= count > 0;
                    }

                    current[key] = count;
                }
            }

            return new RestartSnapshot(
                increased ? aggregate - 1 : aggregate,
                current);
        }

        public void CommitRestarts(RestartSnapshot snapshot) =>
            _restartCounts = snapshot.Counts;

        private static string ReadPodIdentity(V1Pod pod, int index)
        {
            if (!string.IsNullOrWhiteSpace(pod.Metadata?.Uid))
            {
                return $"uid:{pod.Metadata.Uid}";
            }

            if (!string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            {
                return $"name:{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name}";
            }

            return $"index:{index}";
        }

        private static string ReadContainerIdentity(
            V1ContainerStatus status,
            int index) =>
            string.IsNullOrWhiteSpace(status.Name)
                ? $"index:{index}"
                : $"name:{status.Name}";

        private static string? ReadWorkloadRevision(
            IKubernetesObject<V1ObjectMeta> workload)
        {
            V1PodTemplateSpec? template = workload switch
            {
                V1Deployment deployment => deployment.Spec?.Template,
                V1StatefulSet statefulSet => statefulSet.Spec?.Template,
                V1DaemonSet daemonSet => daemonSet.Spec?.Template,
                V1Job job => job.Spec?.Template,
                _ => null,
            };
            string? revision = null;
            _ = template?.Metadata?.Annotations?.TryGetValue(
                KubernetesMetadata.WorkloadRevisionAnnotation,
                out revision);
            return revision;
        }
    }

    private sealed class MutationLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public MutationLease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class TeardownOutcome
    {
        public TeardownOutcome(IApplicationModel model, bool mayCommit)
        {
            Model = model;
            MayCommit = mayCommit;
        }

        public IApplicationModel Model { get; }
        public bool MayCommit { get; }
        public HashSet<string> Expected { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
        public bool CanCommit =>
            MayCommit
            && Failed.Count == 0
            && Expected.Count > 0
            && Expected.SetEquals(Succeeded);
    }
}
