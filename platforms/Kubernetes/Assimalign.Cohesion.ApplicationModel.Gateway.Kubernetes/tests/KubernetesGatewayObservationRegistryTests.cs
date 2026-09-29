using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesGatewayObservationRegistryTests
{
    private static readonly IReadOnlySet<ResourceLifecycle> Running =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running };
    private static readonly IReadOnlySet<ResourceLifecycle> Degraded =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Degraded };

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should resync workload, pods, and services without pod events")]
    public async Task ObserveAsync_OnStatusChangesWithoutPodEvents_ShouldRefreshStateAndExport()
    {
        V1Deployment desiredWorkload = CreateDeployment("worker", ready: true);
        V1Service desiredService = CreateService("worker", host: null);
        var api = new FakeApi(
            CreateDeployment("worker", ready: true),
            CreateService("worker", host: null),
            CreatePods(ready: true),
            CreateServiceEndpoints("worker", ready: false));
        var stateManager = new RecordingStateManager();
        var context = new FakeContext("worker", stateManager);
        var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;
        int runtimeRefreshCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            (_, _) =>
            {
                Interlocked.Increment(ref refreshCount);
                refreshed.TrySetResult(true);
                return Task.CompletedTask;
            },
            readinessBudget: TimeSpan.FromMilliseconds(200),
            refreshRuntime: (_, compilation, _, _) =>
            {
                Interlocked.Increment(ref runtimeRefreshCount);
                return Task.FromResult(compilation);
            });

        registry.Start();
        try
        {
            registry.Register(
                context,
                CreateCompilation(desiredWorkload, desiredService, "http"));
            await api.FirstEndpointsRead.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => stateManager.WriteCount >= 2, TimeSpan.FromSeconds(2));
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Starting);

            api.SetObserved(
                CreateDeployment("worker", ready: true),
                CreateService("worker", "worker.example.test"),
                CreatePods(ready: true),
                CreateServiceEndpoints("worker", ready: true));

            ResourceLifecycle state = await context.State.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2));
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => api.ServiceReadCount >= 4, TimeSpan.FromSeconds(2));

            state.ShouldBe(ResourceLifecycle.Running);
            ResourceEndpoint publicEndpoint = context.State
                .GetObservedEndpoints(context.Resource.Id)
                .Single(endpoint => endpoint.IsPublic);
            publicEndpoint.Host.ShouldBe("worker.example.test");
            publicEndpoint.Port.ShouldBe(80);
            api.WorkloadReadCount.ShouldBeGreaterThanOrEqualTo(4);
            api.PodListCount.ShouldBeGreaterThanOrEqualTo(4);
            api.EndpointsReadCount.ShouldBeGreaterThanOrEqualTo(4);
            Volatile.Read(ref refreshCount).ShouldBe(1);
            Volatile.Read(ref runtimeRefreshCount).ShouldBe(1);
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should publish rollout state before admitting a changed public endpoint contract")]
    public async Task ObserveAsync_OnPublicUrlRollout_ShouldPublishDegradedBeforeNewPodsAreReady()
    {
        V1Deployment oldWorkload = CreateDeployment(
            "worker",
            ready: true,
            workloadRevision: "old-revision");
        V1Deployment newWorkload = CreateDeployment(
            "worker",
            ready: true,
            workloadRevision: "new-revision");
        V1Service desiredService = CreateService("worker", host: null);
        var api = new FakeApi(
            oldWorkload,
            CreateService("worker", host: null),
            new V1PodList
            {
                Items = [CreatePod(ready: true, workloadRevision: "old-revision")],
            },
            CreateServiceEndpoints("worker", ready: true));
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200),
            refreshRuntime: (_, _, _, _) => Task.FromResult(
                CreateCompilation(newWorkload, desiredService, "http")));

        registry.Start();
        try
        {
            registry.Register(
                context,
                CreateCompilation(oldWorkload, desiredService, "http"));
            _ = await context.State.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2));
            int writeIndex = state.WriteCount;

            api.SetObserved(
                oldWorkload,
                CreateService("worker", "worker.example.test"),
                new V1PodList
                {
                    Items = [CreatePod(ready: true, workloadRevision: "old-revision")],
                },
                CreateServiceEndpoints("worker", ready: true));

            await WaitUntilAsync(
                () => state.GetWritesAfter(writeIndex).Any(write =>
                    write.Detail?.Contains(
                        "public endpoint contract changed",
                        StringComparison.Ordinal) is true),
                TimeSpan.FromSeconds(2));
            StateWrite rollout = state.GetWritesAfter(writeIndex).First(write =>
                write.Detail?.Contains(
                    "public endpoint contract changed",
                    StringComparison.Ordinal) is true);
            rollout.State.ShouldBe(ResourceLifecycle.Degraded);
            rollout.Endpoints.ShouldContain(endpoint => endpoint.IsPublic);
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should reject writes from a replaced registration")]
    public async Task Register_OnReplacementDuringObservation_ShouldRejectStaleWrite()
    {
        V1Deployment oldWorkload = CreateDeployment("worker", ready: true);
        V1Deployment newWorkload = CreateDeployment("worker", ready: true);
        var api = new FakeApi(newWorkload, service: null, CreatePods(ready: true), endpoints: null)
        {
            BlockedResource = oldWorkload,
        };
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            registry.Register(context, CreateCompilation(oldWorkload, service: null, "old"));
            await api.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            registry.Register(context, CreateCompilation(newWorkload, service: null, "new"));
            int replacementWrite = state.WriteCount;
            api.ReleaseBlockedRead.TrySetResult(true);

            ResourceLifecycle observed = await state.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2));

            observed.ShouldBe(ResourceLifecycle.Running);
            StateWrite[] writes = state.GetWritesAfter(replacementWrite);
            writes.ShouldNotBeEmpty();
            writes.ShouldAllBe(write =>
                write.Endpoints.All(endpoint => endpoint.Name != "old"));
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should preserve ready state across level-triggered registration")]
    public async Task Register_OnReadyReplacement_ShouldPreserveObservationHistory()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        var api = new FakeApi(
            workload,
            service: null,
            CreatePods(ready: true),
            endpoints: null);
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            KubernetesPlanCompilation compilation = CreateCompilation(
                workload,
                service: null,
                "http");
            registry.Register(context, compilation);
            (await state.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);
            int replacementWrite = state.WriteCount;

            registry.Register(context, compilation);

            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Running);
            await WaitUntilAsync(
                () => state.WriteCount > replacementWrite,
                TimeSpan.FromSeconds(2));
            state.GetWritesAfter(replacementWrite)
                .ShouldAllBe(write => write.State == ResourceLifecycle.Running);
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should reset the restart baseline for a new workload revision")]
    public async Task Register_OnFirstRestartInNewWorkloadRevision_ShouldPublishDegraded()
    {
        V1Deployment oldWorkload = CreateDeployment("worker", ready: true);
        oldWorkload.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesMetadata.WorkloadRevisionAnnotation] = "old",
                },
            },
        };
        var oldPods = new V1PodList
        {
            Items = [CreatePod(ready: true, workloadRevision: "old", restartCount: 3)],
        };
        var api = new FakeApi(oldWorkload, service: null, oldPods, endpoints: null);
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromSeconds(10));

        registry.Start();
        try
        {
            registry.Register(
                context,
                CreateCompilation(oldWorkload, service: null, "http"));
            (await state.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);

            V1Deployment newWorkload = CreateDeployment("worker", ready: true);
            newWorkload.Spec.Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta
                {
                    Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [KubernetesMetadata.WorkloadRevisionAnnotation] = "new",
                    },
                },
            };
            var newPods = new V1PodList
            {
                Items = [CreatePod(ready: true, workloadRevision: "new", restartCount: 1)],
            };
            api.SetObserved(newWorkload, service: null, newPods, endpoints: null);
            registry.Register(
                context,
                CreateCompilation(newWorkload, service: null, "http"));

            (await state.WaitForStateAsync(
                context.Resource.Id,
                Degraded,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Degraded);
            state.LatestWrite.Detail!.ShouldContain("restartCount=1");
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should detect the first restart from a replacement pod in the same revision")]
    public async Task ObserveAsync_OnReplacementPodRestartInSameRevision_ShouldPublishDegraded()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        workload.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesMetadata.WorkloadRevisionAnnotation] = "current",
                },
            },
        };
        var oldPods = new V1PodList
        {
            Items =
            [
                CreatePod(
                    ready: true,
                    workloadRevision: "current",
                    restartCount: 10,
                    podUid: "old-pod"),
            ],
        };
        var api = new FakeApi(workload, service: null, oldPods, endpoints: null);
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromSeconds(10));

        registry.Start();
        try
        {
            registry.Register(
                context,
                CreateCompilation(workload, service: null, "http"));
            (await state.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);

            var replacementPods = new V1PodList
            {
                Items =
                [
                    CreatePod(
                        ready: true,
                        workloadRevision: "current",
                        restartCount: 1,
                        podUid: "new-pod"),
                ],
            };
            api.SetObserved(workload, service: null, replacementPods, endpoints: null);

            (await state.WaitForStateAsync(
                context.Resource.Id,
                Degraded,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Degraded);
            state.LatestWrite.Detail!.ShouldContain("restartCount=1");
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should select pods by the normalized compiled resource label")]
    public async Task ObserveAsync_OnMixedCaseResourceName_ShouldUseCompiledResourceLabel()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        var api = new FakeApi(
            workload,
            service: null,
            CreatePods(ready: true),
            endpoints: null);
        var context = new FakeContext("Worker");
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            registry.Register(context, CreateCompilation(workload, service: null, "http"));

            (await context.State.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);
            api.LastPodLabelSelector.ShouldBe(
                $"{KubernetesMetadata.ResourceLabel}=worker");
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should recover with bounded resync after a pod watch fails")]
    public async Task ObserveAsync_OnWatchFailure_ShouldRetryWithoutStrandingState()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        var api = new FakeApi(
            workload,
            service: null,
            CreatePods(ready: true),
            endpoints: null)
        {
            WatchFailuresRemaining = 1,
        };
        var context = new FakeContext("worker");
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            registry.Register(context, CreateCompilation(workload, service: null, "http"));

            (await context.State.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);
            await WaitUntilAsync(() => api.WatchCallCount >= 2, TimeSpan.FromSeconds(3));
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should ignore failed pods from an older workload revision")]
    public async Task ObserveAsync_OnOverlappingRolloutPods_ShouldEvaluateDesiredRevisionOnly()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        workload.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesMetadata.WorkloadRevisionAnnotation] = "new",
                },
            },
        };
        var pods = new V1PodList
        {
            Items =
            [
                CreatePod(ready: false, workloadRevision: "old", exitCode: 64),
                CreatePod(ready: true, workloadRevision: "new"),
            ],
        };
        var api = new FakeApi(workload, service: null, pods, endpoints: null);
        var context = new FakeContext("worker");
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            registry.Register(context, CreateCompilation(workload, service: null, "http"));

            (await context.State.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should await an in-flight observation when stopping")]
    public async Task StopAsync_OnInFlightObservation_ShouldAwaitItsCancellation()
    {
        V1Deployment workload = CreateDeployment("worker", ready: false);
        var api = new FakeApi(workload, service: null, CreatePods(ready: false), endpoints: null)
        {
            BlockedResource = workload,
            BlockUntilCancelled = true,
        };
        var context = new FakeContext("worker");
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        registry.Register(context, CreateCompilation(workload, service: null, "http"));
        await api.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await registry.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        api.BlockedReadCompleted.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should degrade when a ready workload disappears")]
    public async Task ObserveAsync_OnMissingPreviouslyReadyWorkload_ShouldPublishDegraded()
    {
        V1Deployment workload = CreateDeployment("worker", ready: true);
        var api = new FakeApi(
            workload,
            service: null,
            CreatePods(ready: true),
            endpoints: null);
        var state = new RecordingStateManager();
        var context = new FakeContext("worker", state);
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => { },
            readinessBudget: TimeSpan.FromMilliseconds(200));

        registry.Start();
        try
        {
            registry.Register(context, CreateCompilation(workload, service: null, "http"));
            (await state.WaitForStateAsync(
                context.Resource.Id,
                Running,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);

            api.SetWorkloadMissing();

            (await state.WaitForStateAsync(
                context.Resource.Id,
                Degraded,
                TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Degraded);
            state.LatestWrite.Detail!.ShouldContain("was not found");
        }
        finally
        {
            await registry.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should commit teardown only after every model resource succeeds")]
    public async Task Unregister_OnMultiResourceTeardownSuccess_ShouldCommitAtSessionStop()
    {
        (FakeContext first, FakeContext second) = CreateTeardownContexts();
        var api = new FakeApi(
            CreateDeployment("first", ready: true),
            service: null,
            CreatePods(ready: true),
            endpoints: null);
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => Interlocked.Increment(ref teardownCount));
        registry.Start();
        registry.Register(
            first,
            CreateCompilation(CreateDeployment("first", ready: true), service: null, "first"));
        registry.Register(
            second,
            CreateCompilation(CreateDeployment("second", ready: true), service: null, "second"));

        registry.BeginTeardown(first);
        registry.BeginTeardown(second);
        registry.Unregister(first);
        registry.Unregister(second);

        Volatile.Read(ref teardownCount).ShouldBe(0);
        await registry.StopAsync();
        Volatile.Read(ref teardownCount).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should preserve the namespace after any resource failure")]
    public async Task Stop_OnOneMultiResourceTeardownFailure_ShouldPreserveNamespace()
    {
        (FakeContext first, FakeContext second) = CreateTeardownContexts();
        var api = new FakeApi(
            CreateDeployment("first", ready: true),
            service: null,
            CreatePods(ready: true),
            endpoints: null);
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            api,
            _ => Interlocked.Increment(ref teardownCount));
        registry.Start();
        registry.Register(
            first,
            CreateCompilation(CreateDeployment("first", ready: true), service: null, "first"));
        registry.Register(
            second,
            CreateCompilation(CreateDeployment("second", ready: true), service: null, "second"));

        registry.BeginTeardown(first);
        registry.BeginTeardown(second);
        registry.Unregister(first);
        registry.Stop(second);
        await registry.StopAsync();
        Volatile.Read(ref teardownCount).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should commit explicit uninstall independently of model run mode")]
    public async Task BeginTeardown_OnRunModeDeleteSuccess_ShouldCommitAtSessionStop()
    {
        var context = new FakeContext("worker");
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("worker", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.BeginTeardown(context);
        registry.Unregister(context);
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should invalidate completed teardown when the resource is reconciled again")]
    public async Task Register_AfterCompletedResourceTeardown_ShouldPreserveNamespace()
    {
        var context = new FakeContext("worker");
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("worker", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.Register(
            context,
            CreateCompilation(CreateDeployment("worker", ready: true), service: null, "worker"));
        registry.BeginTeardown(context);
        registry.Unregister(context);
        registry.Register(
            context,
            CreateCompilation(CreateDeployment("worker", ready: false), service: null, "worker"));
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should preserve namespace after explicit uninstall failure")]
    public async Task BeginTeardown_OnRunModeDeleteFailure_ShouldBlockCommit()
    {
        var context = new FakeContext("worker");
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("worker", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.BeginTeardown(context);
        registry.Stop(context);
        await registry.StopAsync();
        Volatile.Read(ref teardownCount).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should not infer teardown from model run mode during stop")]
    public async Task Stop_OnTeardownRunModeWithoutDelete_ShouldNotStartNamespaceTeardown()
    {
        (FakeContext context, _) = CreateTeardownContexts();
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("first", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.Register(
            context,
            CreateCompilation(CreateDeployment("first", ready: true), service: null, "first"));
        registry.Stop(context);
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should commit an explicit uninstall after a successful retry")]
    public async Task BeginTeardown_OnRetryAfterFailure_ShouldReplaceThePriorOutcome()
    {
        var context = new FakeContext("worker");
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("worker", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.BeginTeardown(context);
        registry.Stop(context);
        await registry.StopAsync();
        registry.Start();
        registry.BeginTeardown(context);
        registry.Unregister(context);
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should scope startup rollback to resources reached by the controller")]
    public async Task StopAsync_OnPartialStartupRollback_ShouldCommitReachedResources()
    {
        (FakeContext first, _) = CreateTeardownContexts();
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("first", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount));

        registry.Start();
        registry.Register(
            first,
            CreateCompilation(CreateDeployment("first", ready: true), service: null, "first"));
        registry.BeginTeardown(first);
        registry.Unregister(first);
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Observer: Should preserve a namespace containing custom-controlled resources")]
    public async Task StopAsync_OnCustomControlledModel_ShouldPreserveNamespace()
    {
        var context = new FakeContext("worker");
        int teardownCount = 0;
        var registry = new KubernetesGatewayObservationRegistry(
            new FakeApi(
                CreateDeployment("worker", ready: true),
                service: null,
                CreatePods(ready: true),
                endpoints: null),
            _ => Interlocked.Increment(ref teardownCount),
            canCommitTeardown: _ => false);

        registry.Start();
        registry.Register(
            context,
            CreateCompilation(CreateDeployment("worker", ready: true), service: null, "worker"));
        registry.BeginTeardown(context);
        registry.Unregister(context);
        await registry.StopAsync();

        Volatile.Read(ref teardownCount).ShouldBe(0);
    }

    private static KubernetesPlanCompilation CreateCompilation(
        V1Deployment workload,
        V1Service? service,
        string endpointName)
    {
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects = service is null
            ? [workload]
            : [service, workload];
        return new KubernetesPlanCompilation(
            "appa",
            "plan-hash",
            objects,
            new KubernetesReadinessRule(WorkloadKind.Deployment, 1),
            [new ResourceEndpoint(endpointName, "http", 8080)],
            []);
    }

    private static (FakeContext First, FakeContext Second) CreateTeardownContexts()
    {
        var first = new FakeResource("first");
        var second = new FakeResource("second");
        var model = new FakeModel([first, second], GatewayRunMode.Teardown);
        return (
            new FakeContext(first, model),
            new FakeContext(second, model));
    }

    private static V1Deployment CreateDeployment(
        string name,
        bool ready,
        string? workloadRevision = null) => new()
        {
            ApiVersion = V1Deployment.KubeApiVersion,
            Kind = V1Deployment.KubeKind,
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = "appa",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesMetadata.ResourceLabel] = name.ToLowerInvariant(),
                },
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Template = workloadRevision is null
                ? null
                : new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [KubernetesMetadata.WorkloadRevisionAnnotation] = workloadRevision,
                        },
                    },
                },
            },
            Status = new V1DeploymentStatus
            {
                AvailableReplicas = ready ? 1 : 0,
                UpdatedReplicas = ready ? 1 : 0,
            },
        };

    private static V1Service CreateService(string name, string? host) => new()
    {
        ApiVersion = V1Service.KubeApiVersion,
        Kind = V1Service.KubeKind,
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = "appa",
        },
        Spec = new V1ServiceSpec
        {
            Type = "LoadBalancer",
            Ports = [new V1ServicePort { Name = "http", Port = 80 }],
        },
        Status = new V1ServiceStatus
        {
            LoadBalancer = new V1LoadBalancerStatus
            {
                Ingress = host is null
                    ? []
                    : [new V1LoadBalancerIngress { Hostname = host }],
            },
        },
    };

    private static V1PodList CreatePods(bool ready) => new()
    {
        Items = [CreatePod(ready)],
    };

    private static V1Pod CreatePod(
        bool ready,
        string? workloadRevision = null,
        int? exitCode = null,
        int restartCount = 0,
        string? podUid = null)
    {
        var status = new V1ContainerStatus
        {
            Name = "worker",
            Ready = ready,
            RestartCount = restartCount,
        };
        if (exitCode is int value)
        {
            status.State = new V1ContainerState
            {
                Terminated = new V1ContainerStateTerminated { ExitCode = value },
            };
        }

        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Uid = podUid,
                Annotations = workloadRevision is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [KubernetesMetadata.WorkloadRevisionAnnotation] = workloadRevision,
                    },
            },
            Status = new V1PodStatus
            {
                Conditions =
                [
                    new V1PodCondition
                    {
                        Type = "Ready",
                        Status = ready ? "True" : "False",
                    },
                ],
                ContainerStatuses = [status],
            },
        };
    }

    private static V1Endpoints CreateServiceEndpoints(string name, bool ready) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = "appa",
        },
        Subsets = ready
            ?
            [
                new V1EndpointSubset
                {
                    Addresses = [new V1EndpointAddress { Ip = "10.0.0.10" }],
                },
            ]
            : [],
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), stop.Token);
        }
    }

    private sealed class FakeApi : IKubernetesResourceApi
    {
        private readonly object _sync = new();
        private V1Deployment _workload;
        private V1Service? _service;
        private V1PodList _pods;
        private V1Endpoints? _endpoints;
        private int _workloadReadCount;
        private int _serviceReadCount;
        private int _podListCount;
        private int _endpointsReadCount;
        private int _workloadMissing;
        private string? _lastPodLabelSelector;
        private int _watchCallCount;
        private int _watchFailuresRemaining;
        private readonly TaskCompletionSource<bool> _firstEndpointsRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeApi(
            V1Deployment workload,
            V1Service? service,
            V1PodList pods,
            V1Endpoints? endpoints)
        {
            _workload = workload;
            _service = service;
            _pods = pods;
            _endpoints = endpoints;
        }

        public IKubernetesObject<V1ObjectMeta>? BlockedResource { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public TaskCompletionSource<bool> BlockedReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockedReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseBlockedRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstEndpointsRead => _firstEndpointsRead.Task;
        public int WorkloadReadCount => Volatile.Read(ref _workloadReadCount);
        public int ServiceReadCount => Volatile.Read(ref _serviceReadCount);
        public int PodListCount => Volatile.Read(ref _podListCount);
        public int EndpointsReadCount => Volatile.Read(ref _endpointsReadCount);
        public string? LastPodLabelSelector
        {
            get
            {
                lock (_sync)
                {
                    return _lastPodLabelSelector;
                }
            }
        }
        public int WatchFailuresRemaining
        {
            get => Volatile.Read(ref _watchFailuresRemaining);
            init => _watchFailuresRemaining = value;
        }
        public int WatchCallCount => Volatile.Read(ref _watchCallCount);

        public Task<bool> TryCreateAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ApplyAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            bool force,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task JsonPatchAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            string patch,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
            string namespaceName,
            string? resourceLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>>([]);

        public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
            string namespaceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<V1PersistentVolumeClaim>>([]);

        public async Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(resource, BlockedResource))
            {
                BlockedReadStarted.TrySetResult(true);
                try
                {
                    if (BlockUntilCancelled)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    else
                    {
                        await ReleaseBlockedRead.Task.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    BlockedReadCompleted.TrySetResult(true);
                }
            }

            lock (_sync)
            {
                if (resource is V1Service)
                {
                    Interlocked.Increment(ref _serviceReadCount);
                    return _service;
                }

                Interlocked.Increment(ref _workloadReadCount);
                if (Volatile.Read(ref _workloadMissing) != 0)
                {
                    return null;
                }

                return ReferenceEquals(resource, BlockedResource) ? resource : _workload;
            }
        }

        public Task<V1PodList> ListPodsAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Interlocked.Increment(ref _podListCount);
                _lastPodLabelSelector = labelSelector;
                return Task.FromResult(_pods);
            }
        }

        public Task<V1Endpoints?> ReadEndpointsAsync(
            string namespaceName,
            string serviceName,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Interlocked.Increment(ref _endpointsReadCount);
                _firstEndpointsRead.TrySetResult(true);
                return Task.FromResult(_endpoints);
            }
        }

        public async IAsyncEnumerable<V1Pod> WatchPodsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _watchCallCount);
            if (Interlocked.Decrement(ref _watchFailuresRemaining) >= 0)
            {
                throw new InvalidOperationException("Synthetic expired watch.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public void SetObserved(
            V1Deployment workload,
            V1Service? service,
            V1PodList pods,
            V1Endpoints? endpoints)
        {
            lock (_sync)
            {
                _workload = workload;
                _service = service;
                _pods = pods;
                _endpoints = endpoints;
            }
        }

        public void SetWorkloadMissing() =>
            Interlocked.Exchange(ref _workloadMissing, 1);
    }

    private sealed class RecordingStateManager : IApplicationResourceStateManager
    {
        private readonly object _sync = new();
        private readonly InMemoryResourceStateManager _inner = new();
        private readonly List<StateWrite> _writes = [];

        public int WriteCount
        {
            get
            {
                lock (_sync)
                {
                    return _writes.Count;
                }
            }
        }

        public StateWrite LatestWrite
        {
            get
            {
                lock (_sync)
                {
                    return _writes[^1];
                }
            }
        }

        public event EventHandler<ResourceStateChangedEventArgs>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public ResourceLifecycle GetState(ResourceId id) => _inner.GetState(id);

        public IReadOnlyList<ResourceCommandObservation> GetCommandObservations(ResourceId id) =>
            _inner.GetCommandObservations(id);

        public void SetCommandObservation(ResourceId id, ResourceCommandObservation observation) =>
            _inner.SetCommandObservation(id, observation);

        public void RemoveCommandObservation(ResourceId id, string owner, string commandId) =>
            _inner.RemoveCommandObservation(id, owner, commandId);

        public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id) =>
            _inner.GetObservedEndpoints(id);

        public Task<ResourceLifecycle> WaitForStateAsync(
            ResourceId id,
            IReadOnlySet<ResourceLifecycle> terminals,
            TimeSpan budget,
            CancellationToken cancellationToken = default) =>
            _inner.WaitForStateAsync(id, terminals, budget, cancellationToken);

        public void SetState(
            ResourceId id,
            ResourceLifecycle state,
            string? detail = null,
            IReadOnlyList<ResourceEndpoint>? observedEndpoints = null)
        {
            lock (_sync)
            {
                _writes.Add(new StateWrite(
                    state,
                    detail,
                    observedEndpoints is null
                        ? []
                        : new List<ResourceEndpoint>(observedEndpoints)));
            }

            _inner.SetState(id, state, detail, observedEndpoints);
        }

        public StateWrite[] GetWritesAfter(int index)
        {
            lock (_sync)
            {
                return _writes.Skip(index).ToArray();
            }
        }
    }

    private sealed record StateWrite(
        ResourceLifecycle State,
        string? Detail,
        IReadOnlyList<ResourceEndpoint> Endpoints);

    private sealed class FakeContext : IResourceControlContext
    {
        public FakeContext(string resourceName, IApplicationResourceStateManager? state = null)
        {
            Resource = new FakeResource(resourceName);
            Model = new FakeModel([Resource], GatewayRunMode.Run);
            State = state ?? new InMemoryResourceStateManager();
        }

        public FakeContext(
            IApplicationResource resource,
            IApplicationModel model,
            IApplicationResourceStateManager? state = null)
        {
            Resource = resource;
            Model = model;
            State = state ?? new InMemoryResourceStateManager();
        }

        public IApplicationResourceDescriptor Descriptor => null!;
        public IApplicationResource Resource { get; }
        public ResourcePlan Plan => null!;
        public IApplicationModel Model { get; }
        public IApplicationResourceStateManager State { get; }
        public IReadOnlyList<IApplicationResource> Dependencies => [];
        public ResourceInputs Inputs => ResourceInputs.Empty;
        public IReadOnlyList<ResourceDependencyObservation> ObservedDependencies => [];

        public T GetArtifact<T>() where T : class, IResourceArtifact =>
            throw new InvalidOperationException("This observer-only context has no artifacts.");
    }

    private sealed class FakeResource : IApplicationResource
    {
        public FakeResource(string name)
        {
            Name = name;
        }

        public ResourceName Name { get; }
        public ResourceId Id { get; } = ResourceId.New();
    }

    private sealed class FakeModel : IApplicationModel
    {
        private readonly GatewayRunMode _runMode;

        public FakeModel(
            IReadOnlyList<IApplicationResource> resources,
            GatewayRunMode runMode)
        {
            Resources = resources;
            _runMode = runMode;
        }

        public ApplicationName Name => "appa";
        public IApplicationEnvironment Environment => new FakeEnvironment();
        public GatewayRunMode RunMode => _runMode;
        public ResourceName GatewayIdentity => "kubernetes";
        public string Owner => "appa@kubernetes";
        public bool Adopt => false;
        public bool RestartOrphans => false;
        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => [];
        public IReadOnlyList<IApplicationResource> Resources { get; }
        public IReadOnlyList<ResourceManifest> Manifests => [];
        public IReadOnlyList<ResourcePlan> Plans => [];
    }

    private sealed class FakeEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Local";
        public bool IsLocal => true;
        public bool IsDevelopment => false;
    }
}
