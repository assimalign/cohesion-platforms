using System.Collections.Generic;

using k8s;
using k8s.Models;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesReadinessRuleTests
{
    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should report ready long-running workloads as Running")]
    [InlineData(WorkloadKind.Deployment)]
    [InlineData(WorkloadKind.StatefulSet)]
    [InlineData(WorkloadKind.DaemonSet)]
    public void Evaluate_OnReadyLongRunningWorkload_ShouldReturnRunning(WorkloadKind kind)
    {
        var rule = new KubernetesReadinessRule(kind, 1);

        KubernetesReadinessObservation result = rule.Evaluate(CreateWorkload(kind, 1), [CreatePod(true)], false);

        result.State.ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should report a completed Job as Stopped")]
    public void Evaluate_OnCompletedJob_ShouldReturnStopped()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Job, 1);

        KubernetesReadinessObservation result = rule.Evaluate(CreateWorkload(WorkloadKind.Job, 1), [], false);

        result.State.ShouldBe(ResourceLifecycle.Stopped);
        result.ExitCode.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should report restarts after readiness as Degraded")]
    public void Evaluate_OnRestartAfterReadiness_ShouldReturnDegraded()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Deployment, 0), [CreatePod(false, restartCount: 2)], true);

        result.State.ShouldBe(ResourceLifecycle.Degraded);
        result.RestartCount.ShouldBe(2);
        result.Detail!.ShouldContain("restartCount=2");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should recover after restart count stabilizes")]
    public void Evaluate_OnUnchangedRestartCountAndHealthyWorkload_ShouldReturnRunning()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Deployment, 1),
            [CreatePod(true, restartCount: 2)],
            wasReady: true,
            endpointsReady: true,
            previousRestartCount: 2);

        result.State.ShouldBe(ResourceLifecycle.Running);
        result.RestartCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should require ready Service endpoints")]
    [InlineData(false, ResourceLifecycle.Starting)]
    [InlineData(true, ResourceLifecycle.Degraded)]
    public void Evaluate_OnMissingServiceEndpoints_ShouldWithholdRunning(
        bool wasReady,
        ResourceLifecycle expected)
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Deployment, 1),
            [CreatePod(true)],
            wasReady,
            endpointsReady: false);

        result.State.ShouldBe(expected);
        result.Detail!.ShouldContain("Service endpoints");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should surface a failed container exit code")]
    public void Evaluate_OnFailedContainer_ShouldSurfaceExitCode()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Job, 1);

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Job, 0), [CreatePod(false, exitCode: 17)], false);

        result.State.ShouldBe(ResourceLifecycle.Failed);
        result.ExitCode.ShouldBe(17);
        result.Detail!.ShouldContain("exitCode=17");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should surface the last terminated exit after a restart")]
    public void Evaluate_OnRestartedContainer_ShouldSurfaceLastExitAndDegrade()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);
        V1Pod pod = CreatePod(true, restartCount: 1);
        pod.Status.ContainerStatuses[0].LastState = new V1ContainerState
        {
            Terminated = new V1ContainerStateTerminated { ExitCode = 17 },
        };

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Deployment, 1), [pod], true);

        result.State.ShouldBe(ResourceLifecycle.Degraded);
        result.ExitCode.ShouldBe(17);
        result.RestartCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should fail a fatal configuration exit after readiness")]
    public void Evaluate_OnFatalContainerExit_ShouldReturnFailed()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);
        V1Pod pod = CreatePod(false, restartCount: 1);
        pod.Status.ContainerStatuses[0].LastState = new V1ContainerState
        {
            Terminated = new V1ContainerStateTerminated { ExitCode = 64 },
        };

        KubernetesReadinessObservation result = rule.Evaluate(
            CreateWorkload(WorkloadKind.Deployment, 0), [pod], true);

        result.State.ShouldBe(ResourceLifecycle.Failed);
        result.ExitCode.ShouldBe(64);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should reject stale controller status")]
    public void Evaluate_OnStaleDeploymentStatus_ShouldRemainStarting()
    {
        var rule = new KubernetesReadinessRule(WorkloadKind.Deployment, 1);
        var workload = (V1Deployment)CreateWorkload(WorkloadKind.Deployment, 1);
        workload.Metadata = new V1ObjectMeta { Generation = 2 };
        workload.Status.ObservedGeneration = 1;

        KubernetesReadinessObservation result = rule.Evaluate(workload, [CreatePod(true)], false);

        result.State.ShouldBe(ResourceLifecycle.Starting);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should ignore stale controller failure conditions")]
    [InlineData(WorkloadKind.Deployment)]
    [InlineData(WorkloadKind.StatefulSet)]
    [InlineData(WorkloadKind.DaemonSet)]
    public void Evaluate_OnStaleControllerFailure_ShouldRemainStarting(WorkloadKind kind)
    {
        var rule = new KubernetesReadinessRule(kind, 1);
        IKubernetesObject<V1ObjectMeta> workload = CreateFailedWorkload(kind, generation: 2, observedGeneration: 1);

        KubernetesReadinessObservation result = rule.Evaluate(workload, [CreatePod(false)], false);

        result.State.ShouldBe(ResourceLifecycle.Starting);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Readiness: Should honor current controller failure conditions")]
    [InlineData(WorkloadKind.Deployment)]
    [InlineData(WorkloadKind.StatefulSet)]
    [InlineData(WorkloadKind.DaemonSet)]
    public void Evaluate_OnCurrentControllerFailure_ShouldReturnFailed(WorkloadKind kind)
    {
        var rule = new KubernetesReadinessRule(kind, 1);
        IKubernetesObject<V1ObjectMeta> workload = CreateFailedWorkload(kind, generation: 2, observedGeneration: 2);

        KubernetesReadinessObservation result = rule.Evaluate(workload, [CreatePod(false)], false);

        result.State.ShouldBe(ResourceLifecycle.Failed);
    }

    private static IKubernetesObject<V1ObjectMeta> CreateWorkload(WorkloadKind kind, int ready) => kind switch
    {
        WorkloadKind.Deployment => new V1Deployment { Kind = V1Deployment.KubeKind, Spec = new V1DeploymentSpec { Replicas = 1 }, Status = new V1DeploymentStatus { AvailableReplicas = ready, UpdatedReplicas = ready } },
        WorkloadKind.StatefulSet => new V1StatefulSet { Kind = V1StatefulSet.KubeKind, Spec = new V1StatefulSetSpec { Replicas = 1 }, Status = new V1StatefulSetStatus { ReadyReplicas = ready, CurrentReplicas = ready, UpdatedReplicas = ready } },
        WorkloadKind.DaemonSet => new V1DaemonSet { Kind = V1DaemonSet.KubeKind, Status = new V1DaemonSetStatus { NumberAvailable = ready, NumberReady = ready, UpdatedNumberScheduled = ready, DesiredNumberScheduled = 1 } },
        WorkloadKind.Job => new V1Job { Kind = V1Job.KubeKind, Spec = new V1JobSpec { Completions = 1 }, Status = new V1JobStatus { Succeeded = ready } },
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IKubernetesObject<V1ObjectMeta> CreateFailedWorkload(
        WorkloadKind kind,
        long generation,
        long observedGeneration)
    {
        IKubernetesObject<V1ObjectMeta> workload = CreateWorkload(kind, 0);
        workload.Metadata = new V1ObjectMeta { Generation = generation };

        switch (workload)
        {
            case V1Deployment deployment:
                deployment.Status.ObservedGeneration = observedGeneration;
                deployment.Status.Conditions =
                [
                    new V1DeploymentCondition { Type = "ReplicaFailure", Status = "True" },
                ];
                break;
            case V1StatefulSet statefulSet:
                statefulSet.Status.ObservedGeneration = observedGeneration;
                statefulSet.Status.Conditions =
                [
                    new V1StatefulSetCondition { Type = "Failed", Status = "True" },
                ];
                break;
            case V1DaemonSet daemonSet:
                daemonSet.Status.ObservedGeneration = observedGeneration;
                daemonSet.Status.Conditions =
                [
                    new V1DaemonSetCondition { Type = "Failed", Status = "True" },
                ];
                break;
        }

        return workload;
    }

    private static V1Pod CreatePod(bool ready, int restartCount = 0, int? exitCode = null)
    {
        var container = new V1ContainerStatus { Name = "resource", Ready = ready, RestartCount = restartCount };
        if (exitCode is int value)
        {
            container.State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = value } };
        }

        return new V1Pod
        {
            Status = new V1PodStatus
            {
                Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }],
                ContainerStatuses = new List<V1ContainerStatus> { container },
            },
        };
    }
}
