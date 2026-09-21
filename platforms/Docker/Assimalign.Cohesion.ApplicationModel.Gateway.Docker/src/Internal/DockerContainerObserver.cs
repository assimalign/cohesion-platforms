using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerContainerObserver
{
    private const int ConfigurationExitCode = 64;
    private const int StartupExitCode = 70;

    private readonly object _gate = new();
    private readonly DockerGatewayOptions _options;
    private readonly Func<IDockerEngineClient> _getEngine;
    private readonly Func<
        IResourceControlContext,
        DockerPlanCompilation,
        CancellationToken,
        Task<string>> _restart;
    private readonly Dictionary<DockerResourceKey, Entry> _entries = new();
    private readonly SemaphoreSlim _refresh = new(0, 1);

    private CancellationTokenSource? _lifetime;
    private Task _observation = Task.CompletedTask;
    private Task _events = Task.CompletedTask;
    private DockerProbeRunner? _probes;

    public DockerContainerObserver(
        DockerGatewayOptions options,
        Func<IDockerEngineClient> getEngine,
        Func<IResourceControlContext, DockerPlanCompilation, CancellationToken, Task<string>> restart)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getEngine);
        ArgumentNullException.ThrowIfNull(restart);
        _options = options;
        _getEngine = getEngine;
        _restart = restart;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_lifetime is not null)
            {
                throw new InvalidOperationException("The Docker observer is already running.");
            }

            _entries.Clear();
            _lifetime = new CancellationTokenSource();
            _probes = new DockerProbeRunner(_getEngine(), _options);
            _observation = ObserveLoopAsync(_lifetime.Token);
            _events = EventLoopAsync(_lifetime.Token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        Task observation;
        Task events;
        DockerProbeRunner? probes;
        lock (_gate)
        {
            lifetime = _lifetime;
            if (lifetime is null)
            {
                return;
            }

            _lifetime = null;
            foreach (Entry entry in _entries.Values)
            {
                entry.StopRequested = true;
            }

            observation = _observation;
            events = _events;
            probes = _probes;
            _probes = null;
        }

        lifetime.Cancel();
        SignalRefresh();
        try
        {
            await Task.WhenAll(observation, events).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            probes?.Dispose();
            lifetime.Dispose();
            lock (_gate)
            {
                _entries.Clear();
            }
        }
    }

    public async Task RegisterAsync(
        IResourceControlContext context,
        DockerPlanCompilation compilation,
        string containerId,
        RestartPolicy restartPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new DockerResourceKey(context.Model.Name, context.Resource.Id);
        Entry entry;
        lock (_gate)
        {
            if (_lifetime is null)
            {
                throw new InvalidOperationException("The Docker observer has not been started.");
            }

            if (_entries.TryGetValue(key, out Entry? previous)
                && string.Equals(previous.ContainerId, containerId, StringComparison.Ordinal)
                && string.Equals(
                    previous.Compilation.RuntimeHash,
                    compilation.RuntimeHash,
                    StringComparison.Ordinal))
            {
                previous.Update(context, compilation, restartPolicy);
                entry = previous;
            }
            else
            {
                previous?.Deactivate();
                entry = new Entry(key, context, compilation, containerId, restartPolicy);
                _entries[key] = entry;
            }
        }

        if (!entry.WasRunning)
        {
            TrySetState(entry, ResourceLifecycle.Starting, "Docker container is starting.", []);
        }

        await ObserveEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        SignalRefresh();
    }

    public void BeginStop(IResourceControlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryGetEntry(context, out Entry? entry) && entry is not null)
        {
            entry.StopRequested = true;
        }
    }

    public async ValueTask<IDisposable> EnterMutationAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetEntry(context, out Entry? entry) || entry is null)
        {
            return EmptyMutation.Instance;
        }

        await entry.Mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(entry))
        {
            entry.Mutation.Release();
            return EmptyMutation.Instance;
        }

        return new MutationLease(entry.Mutation);
    }

    public void Unregister(IResourceControlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = new DockerResourceKey(context.Model.Name, context.Resource.Id);
        lock (_gate)
        {
            if (_entries.Remove(key, out Entry? entry))
            {
                entry.Deactivate();
            }
        }
    }

    public void PublishStopped(
        IResourceControlContext context,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        context.State.SetState(
            context.Resource.Id,
            ResourceLifecycle.Stopped,
            detail,
            []);
    }

    public void PublishFailed(
        IResourceControlContext context,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        context.State.SetState(
            context.Resource.Id,
            ResourceLifecycle.Failed,
            detail,
            []);
    }

    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = _options.ObservationInterval < _options.ProbeInterval
            ? _options.ObservationInterval
            : _options.ProbeInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = Task.Delay(interval, _options.TimeProvider, wake.Token);
            Task signal = _refresh.WaitAsync(wake.Token);
            _ = await Task.WhenAny(delay, signal).ConfigureAwait(false);
            wake.Cancel();
            try
            {
                await Task.WhenAll(delay, signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wake.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EventLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (DockerEventMessage message in _getEngine()
                    .GetEventsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (string.Equals(message.Type, "container", StringComparison.OrdinalIgnoreCase))
                    {
                        SignalRefresh();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException)
            {
            }

            await Task.Delay(
                _options.EventReconnectDelay,
                _options.TimeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Values];
        }

        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ObserveEntryAsync(entries[index], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException
                    or InvalidOperationException)
            {
                PublishObservationFailure(entries[index], exception.Message);
            }
        }
    }

    private async Task ObserveEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        await entry.Mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(entry))
            {
                return;
            }

            DockerContainerInspectResponse? inspection = await _getEngine()
                .InspectContainerAsync(entry.ContainerId, cancellationToken)
                .ConfigureAwait(false);
            if (inspection is null)
            {
                TrySetState(
                    entry,
                    ResourceLifecycle.Failed,
                    $"Docker container '{entry.ContainerId}' is missing.",
                    []);
                return;
            }

            IReadOnlyList<ResourceEndpoint> endpoints = CreateObservedEndpoints(
                entry.Context.Plan,
                entry.Compilation,
                inspection,
                _options.PublicHost);
            if (inspection.State?.Running is true)
            {
                await ObserveRunningAsync(entry, inspection, endpoints, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await ObserveExitAsync(entry, inspection, endpoints, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.Mutation.Release();
        }
    }

    private async Task ObserveRunningAsync(
        Entry entry,
        DockerContainerInspectResponse inspection,
        IReadOnlyList<ResourceEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        if (entry.StopRequested)
        {
            return;
        }

        if (!entry.StartupPassed)
        {
            DockerProbePlan? startup = FindProbe(entry.Compilation.Probes, "startup");
            DockerProbeResult result = await RunProbeAsync(
                entry,
                startup,
                inspection,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                TrySetState(
                    entry,
                    result.FailFast ? ResourceLifecycle.Failed : ResourceLifecycle.Starting,
                    result.Detail,
                    endpoints);
                return;
            }

            entry.StartupPassed = true;
        }

        if (!entry.ReadinessPassed)
        {
            DockerProbePlan? readiness = FindProbe(entry.Compilation.Probes, "readiness");
            DockerProbeResult result = await RunProbeAsync(
                entry,
                readiness,
                inspection,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                TrySetState(
                    entry,
                    result.FailFast ? ResourceLifecycle.Failed : ResourceLifecycle.Starting,
                    result.Detail,
                    endpoints);
                return;
            }

            entry.ReadinessPassed = true;
        }

        if (!entry.WasRunning)
        {
            entry.WasRunning = true;
            entry.ConsecutiveLivenessFailures = 0;
            TrySetState(
                entry,
                ResourceLifecycle.Running,
                "Docker container is running and ready.",
                endpoints);
            return;
        }

        DockerProbePlan? liveness = FindProbe(entry.Compilation.Probes, "liveness");
        DockerProbeResult livenessResult = await RunProbeAsync(
            entry,
            liveness,
            inspection,
            cancellationToken).ConfigureAwait(false);
        if (livenessResult.Succeeded)
        {
            entry.ConsecutiveLivenessFailures = 0;
            TrySetState(
                entry,
                ResourceLifecycle.Running,
                "Docker container is running and ready.",
                endpoints);
            return;
        }

        entry.ConsecutiveLivenessFailures++;
        TrySetState(
            entry,
            ResourceLifecycle.Degraded,
            livenessResult.Detail,
            endpoints);
        if (entry.ConsecutiveLivenessFailures < _options.LivenessFailureThreshold
            || entry.RestartPolicy is RestartPolicy.Never)
        {
            return;
        }

        await RestartAsync(entry, livenessResult.Detail ?? "Docker liveness probe failed.", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ObserveExitAsync(
        Entry entry,
        DockerContainerInspectResponse inspection,
        IReadOnlyList<ResourceEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        int exitCode = inspection.State?.ExitCode ?? -1;
        if (entry.StopRequested)
        {
            TrySetState(entry, ResourceLifecycle.Stopped, "Docker container stopped.", []);
            return;
        }

        bool runOnceSucceeded = entry.Compilation.Container.Workload is WorkloadKind.Job
            && exitCode == 0;
        if (runOnceSucceeded)
        {
            TrySetState(
                entry,
                ResourceLifecycle.Stopped,
                "Docker run-once task completed successfully with exit code 0.",
                endpoints);
            return;
        }

        if (!ShouldRestart(entry.RestartPolicy, exitCode))
        {
            ResourceLifecycle state = exitCode == 0
                ? ResourceLifecycle.Stopped
                : ResourceLifecycle.Failed;
            string classification = exitCode is ConfigurationExitCode or StartupExitCode
                ? "final"
                : "not restartable";
            TrySetState(
                entry,
                state,
                $"Docker container exited with code {exitCode.ToString(CultureInfo.InvariantCulture)} ({classification}).",
                endpoints);
            return;
        }

        if (entry.WasRunning)
        {
            TrySetState(
                entry,
                ResourceLifecycle.Degraded,
                $"Docker container exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}; restart scheduled.",
                endpoints);
        }

        await RestartAsync(
            entry,
            $"Docker container exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartAsync(
        Entry entry,
        string detail,
        CancellationToken cancellationToken)
    {
        if (entry.StopRequested || !IsCurrent(entry))
        {
            return;
        }

        entry.RestartAttempts++;
        if (entry.RestartAttempts > _options.MaximumRestartAttempts)
        {
            TrySetState(
                entry,
                ResourceLifecycle.Failed,
                $"Docker restart limit {_options.MaximumRestartAttempts} was exhausted. Last failure: {detail}",
                []);
            return;
        }

        TrySetState(entry, ResourceLifecycle.Stopping, detail, []);
        TimeSpan backoff = CalculateRestartBackoff(
            entry.RestartAttempts,
            _options.InitialRestartBackoff,
            _options.MaximumRestartBackoff);
        using CancellationTokenSource delayCancellation =
            entry.BeginRestartDelay(cancellationToken);
        try
        {
            await Task.Delay(backoff, _options.TimeProvider, delayCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            entry.StopRequested && !cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            entry.EndRestartDelay(delayCancellation);
        }

        if (entry.StopRequested || !IsCurrent(entry))
        {
            return;
        }

        entry.ContainerId = await _restart(entry.Context, entry.Compilation, cancellationToken)
            .ConfigureAwait(false);
        entry.StartupPassed = false;
        entry.ReadinessPassed = false;
        entry.WasRunning = false;
        entry.ConsecutiveLivenessFailures = 0;
        TrySetState(entry, ResourceLifecycle.Starting, "Docker container restarted.", []);
    }

    private async Task<DockerProbeResult> RunProbeAsync(
        Entry entry,
        DockerProbePlan? probe,
        DockerContainerInspectResponse inspection,
        CancellationToken cancellationToken)
    {
        if (probe is null || probe.Kind is ProbeKind.None)
        {
            return DockerProbeResult.Success();
        }

        DockerProbeRunner probes = _probes
            ?? throw new InvalidOperationException("The Docker probe runner is not active.");
        return await probes.RunAsync(entry.ContainerId, probe, inspection, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ShouldRestart(RestartPolicy policy, int exitCode)
    {
        if (policy is RestartPolicy.Never
            || exitCode is ConfigurationExitCode or StartupExitCode)
        {
            return false;
        }

        return policy is RestartPolicy.Always
            || (policy is RestartPolicy.OnFailure && exitCode != 0);
    }

    internal static TimeSpan CalculateRestartBackoff(
        int restartAttempt,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(restartAttempt, 1);
        long ticks = initialBackoff.Ticks;
        for (int attempt = 1; attempt < restartAttempt && ticks < maximumBackoff.Ticks; attempt++)
        {
            ticks = ticks > maximumBackoff.Ticks / 2
                ? maximumBackoff.Ticks
                : ticks * 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, maximumBackoff.Ticks));
    }

    private static DockerProbePlan? FindProbe(
        IReadOnlyList<DockerProbePlan> probes,
        string role)
    {
        for (int index = 0; index < probes.Count; index++)
        {
            if (string.Equals(probes[index].Role, role, StringComparison.Ordinal))
            {
                return probes[index];
            }
        }

        return null;
    }

    private static IReadOnlyList<ResourceEndpoint> CreateObservedEndpoints(
        ResourcePlan plan,
        DockerPlanCompilation compilation,
        DockerContainerInspectResponse inspection,
        string publicHost)
    {
        var endpoints = new List<ResourceEndpoint>(plan.Container.Ports.Count + plan.Exposures.Count);
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            string host = FindServiceHost(plan.Services, port.Endpoint, compilation.Container.Name);
            string scheme = FindScheme(plan, port.Endpoint);
            _ = Uri.CreateEndpoint(scheme, host, port.ContainerPort);
            endpoints.Add(new ResourceEndpoint(
                port.Endpoint,
                scheme,
                port.ContainerPort,
                IsPublic: false,
                Host: host));
        }

        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            ExposureSpec exposure = plan.Exposures[index];
            int hostPort = FindPublishedPort(exposure, inspection);
            _ = Uri.CreateEndpoint(exposure.Scheme, publicHost, hostPort);
            endpoints.Add(new ResourceEndpoint(
                exposure.Endpoint,
                exposure.Scheme,
                hostPort,
                IsPublic: true,
                Host: publicHost));
        }

        return endpoints;
    }

    private static int FindPublishedPort(
        ExposureSpec exposure,
        DockerContainerInspectResponse inspection)
    {
        string key = exposure.Port.ToString(CultureInfo.InvariantCulture)
            + "/" + exposure.Protocol.ToLowerInvariant();
        if (inspection.NetworkSettings?.Ports is not { } ports
            || !ports.TryGetValue(key, out DockerPortBinding[]? bindings)
            || bindings is null)
        {
            throw new InvalidOperationException(
                $"Docker container has no published binding for exposure '{exposure.Name}'.");
        }

        for (int index = 0; index < bindings.Length; index++)
        {
            DockerPortBinding binding = bindings[index];
            if (!IsLoopback(binding.HostIp)
                && int.TryParse(
                    binding.HostPort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int port)
                && port is >= 1 and <= 65535)
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"Docker container has no public host binding for exposure '{exposure.Name}'.");
    }

    private static string FindServiceHost(
        IReadOnlyList<ServiceSpec> services,
        string endpoint,
        string fallback)
    {
        for (int index = 0; index < services.Count; index++)
        {
            if (string.Equals(services[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return DockerMetadata.Normalize(services[index].Name);
            }
        }

        return fallback;
    }

    private static string FindScheme(ResourcePlan plan, string endpoint)
    {
        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            if (string.Equals(port.Endpoint, endpoint, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(port.Scheme))
            {
                return port.Scheme;
            }
        }

        for (int index = 0; index < plan.Exposures.Count; index++)
        {
            if (string.Equals(plan.Exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return plan.Exposures[index].Scheme;
            }
        }

        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping probe = plan.Container.Probes[index];
            if (probe.Kind is ProbeKind.Http
                && string.Equals(probe.Endpoint, endpoint, StringComparison.Ordinal))
            {
                return "http";
            }
        }

        for (int index = 0; index < plan.Container.Ports.Count; index++)
        {
            PortBinding port = plan.Container.Ports[index];
            if (string.Equals(port.Endpoint, endpoint, StringComparison.Ordinal))
            {
                return string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp";
            }
        }

        throw new InvalidDataException($"Docker endpoint '{endpoint}' has no port binding.");
    }

    private static bool IsLoopback(string? host) =>
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    private void PublishObservationFailure(Entry entry, string detail)
    {
        ResourceLifecycle state = entry.WasRunning
            ? ResourceLifecycle.Degraded
            : ResourceLifecycle.Starting;
        TrySetState(entry, state, $"Docker observation failed: {detail}", []);
    }

    private bool TryGetEntry(IResourceControlContext context, out Entry? entry)
    {
        var key = new DockerResourceKey(context.Model.Name, context.Resource.Id);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out entry);
        }
    }

    private bool IsCurrent(Entry entry)
    {
        lock (_gate)
        {
            return entry.Active
                && _entries.TryGetValue(entry.Key, out Entry? current)
                && ReferenceEquals(entry, current);
        }
    }

    private bool TrySetState(
        Entry entry,
        ResourceLifecycle state,
        string? detail,
        IReadOnlyList<ResourceEndpoint> endpoints)
    {
        lock (_gate)
        {
            if (!entry.Active
                || !_entries.TryGetValue(entry.Key, out Entry? current)
                || !ReferenceEquals(entry, current))
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

    private void SignalRefresh()
    {
        try
        {
            _refresh.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private readonly record struct DockerResourceKey(
        ApplicationName Application,
        ResourceId Resource);

    private sealed class Entry
    {
        private readonly object _restartDelayGate = new();
        private CancellationTokenSource? _restartDelay;
        private int _stopRequested;

        public Entry(
            DockerResourceKey key,
            IResourceControlContext context,
            DockerPlanCompilation compilation,
            string containerId,
            RestartPolicy restartPolicy)
        {
            Key = key;
            Context = context;
            Compilation = compilation;
            ContainerId = containerId;
            RestartPolicy = restartPolicy;
        }

        public DockerResourceKey Key { get; }
        public IResourceControlContext Context { get; private set; }
        public DockerPlanCompilation Compilation { get; private set; }
        public RestartPolicy RestartPolicy { get; private set; }
        public SemaphoreSlim Mutation { get; } = new(1, 1);
        public string ContainerId { get; set; }
        public bool Active { get; private set; } = true;
        public bool StopRequested
        {
            get => Volatile.Read(ref _stopRequested) != 0;
            set
            {
                Volatile.Write(ref _stopRequested, value ? 1 : 0);
                if (!value)
                {
                    return;
                }

                lock (_restartDelayGate)
                {
                    _restartDelay?.Cancel();
                }
            }
        }
        public bool StartupPassed { get; set; }
        public bool ReadinessPassed { get; set; }
        public bool WasRunning { get; set; }
        public int ConsecutiveLivenessFailures { get; set; }
        public int RestartAttempts { get; set; }

        public CancellationTokenSource BeginRestartDelay(CancellationToken cancellationToken)
        {
            var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_restartDelayGate)
            {
                if (StopRequested)
                {
                    delay.Cancel();
                }

                _restartDelay = delay;
            }

            return delay;
        }

        public void EndRestartDelay(CancellationTokenSource delay)
        {
            lock (_restartDelayGate)
            {
                if (ReferenceEquals(_restartDelay, delay))
                {
                    _restartDelay = null;
                }
            }
        }

        public void Update(
            IResourceControlContext context,
            DockerPlanCompilation compilation,
            RestartPolicy restartPolicy)
        {
            Context = context;
            Compilation = compilation;
            RestartPolicy = restartPolicy;
            StopRequested = false;
        }

        public void Deactivate()
        {
            Active = false;
            StopRequested = true;
        }
    }

    private sealed class MutationLease : IDisposable
    {
        private SemaphoreSlim? _mutation;

        public MutationLease(SemaphoreSlim mutation) => _mutation = mutation;

        public void Dispose() => Interlocked.Exchange(ref _mutation, null)?.Release();
    }

    private sealed class EmptyMutation : IDisposable
    {
        public static EmptyMutation Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
