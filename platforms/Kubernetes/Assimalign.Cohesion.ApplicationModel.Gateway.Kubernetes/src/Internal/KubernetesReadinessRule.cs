using System;
using System.Collections.Generic;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed record KubernetesReadinessObservation(
    ResourceLifecycle State,
    string? Detail,
    int? ExitCode,
    int RestartCount);

internal sealed class KubernetesReadinessRule
{
    public KubernetesReadinessRule(WorkloadKind kind, int replicas)
    {
        Kind = kind;
        Replicas = replicas;
    }

    public WorkloadKind Kind { get; }

    public int Replicas { get; }

    public KubernetesReadinessObservation Evaluate(
        IKubernetesObject<V1ObjectMeta> workload,
        IReadOnlyList<V1Pod> pods,
        bool wasReady,
        bool endpointsReady = true,
        int previousRestartCount = 0)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(pods);

        (int ready, int desired, bool controllerFailed, bool completed) = ReadController(workload);
        (int restarts, int? exitCode) = ReadContainers(pods);
        bool podsReady = PodsReady(pods, desired);
        bool restartIncreased = restarts > previousRestartCount;

        bool terminalExit = exitCode is 64 or 70;
        if (controllerFailed || terminalExit || (Kind is WorkloadKind.Job && exitCode is > 0))
        {
            return new KubernetesReadinessObservation(
                ResourceLifecycle.Failed,
                exitCode is int surfaced
                    ? $"Kubernetes workload failed with exitCode={surfaced}."
                    : "Kubernetes workload controller reported a terminal failure.",
                exitCode,
                restarts);
        }

        if (Kind is WorkloadKind.Job)
        {
            return completed
                ? new KubernetesReadinessObservation(
                    ResourceLifecycle.Stopped,
                    "Kubernetes Job completed successfully with exitCode=0.",
                    0,
                    restarts)
                : new KubernetesReadinessObservation(
                    ResourceLifecycle.Starting,
                    "Kubernetes Job has not completed.",
                    exitCode,
                    restarts);
        }

        if (wasReady && (restartIncreased || ready < desired || !podsReady || !endpointsReady))
        {
            return new KubernetesReadinessObservation(
                ResourceLifecycle.Degraded,
                !endpointsReady
                    ? $"Kubernetes Service endpoints are not Ready; restartCount={restarts}."
                    : ready < desired || !podsReady
                        ? $"Kubernetes workload is not Ready ({ready}/{desired}); restartCount={restarts}."
                        : $"Kubernetes workload observed a new container restart; restartCount={restarts}.",
                exitCode,
                restarts);
        }

        if (ready >= desired && desired > 0 && podsReady && endpointsReady)
        {
            return new KubernetesReadinessObservation(
                ResourceLifecycle.Running,
                null,
                exitCode,
                restarts);
        }

        if (ready >= desired && desired > 0 && podsReady)
        {
            return new KubernetesReadinessObservation(
                ResourceLifecycle.Starting,
                "Kubernetes Service endpoints are not Ready.",
                exitCode,
                restarts);
        }

        if (exitCode is > 0)
        {
            return new KubernetesReadinessObservation(
                ResourceLifecycle.Starting,
                $"Kubernetes workload is restarting after exitCode={exitCode}; restartCount={restarts}.",
                exitCode,
                restarts);
        }

        return new KubernetesReadinessObservation(
            ResourceLifecycle.Starting,
            $"Kubernetes workload is becoming Ready ({ready}/{desired}).",
            exitCode,
            restarts);
    }

    private (int Ready, int Desired, bool Failed, bool Completed) ReadController(
        IKubernetesObject<V1ObjectMeta> workload) => (Kind, workload) switch
        {
            (WorkloadKind.Deployment, V1Deployment deployment) => (
                IsCurrent(deployment.Metadata, deployment.Status?.ObservedGeneration)
                    ? Math.Min(
                        deployment.Status?.AvailableReplicas ?? 0,
                        deployment.Status?.UpdatedReplicas ?? 0)
                    : 0,
                deployment.Spec?.Replicas ?? Replicas,
                IsCurrent(deployment.Metadata, deployment.Status?.ObservedGeneration)
                    && DeploymentFailed(deployment.Status?.Conditions),
                false),
            (WorkloadKind.StatefulSet, V1StatefulSet statefulSet) => (
                IsCurrent(statefulSet.Metadata, statefulSet.Status?.ObservedGeneration)
                    ? Math.Min(
                        statefulSet.Status?.ReadyReplicas ?? 0,
                        Math.Min(
                            statefulSet.Status?.CurrentReplicas ?? 0,
                            statefulSet.Status?.UpdatedReplicas ?? 0))
                    : 0,
                statefulSet.Spec?.Replicas ?? Replicas,
                IsCurrent(statefulSet.Metadata, statefulSet.Status?.ObservedGeneration)
                    && StatefulSetFailed(statefulSet.Status?.Conditions),
                false),
            (WorkloadKind.DaemonSet, V1DaemonSet daemonSet) => (
                IsCurrent(daemonSet.Metadata, daemonSet.Status?.ObservedGeneration)
                    ? Math.Min(
                        daemonSet.Status?.NumberAvailable ?? 0,
                        Math.Min(
                            daemonSet.Status?.NumberReady ?? 0,
                            daemonSet.Status?.UpdatedNumberScheduled ?? 0))
                    : 0,
                daemonSet.Status?.DesiredNumberScheduled ?? Replicas,
                IsCurrent(daemonSet.Metadata, daemonSet.Status?.ObservedGeneration)
                    && DaemonSetFailed(daemonSet.Status?.Conditions),
                false),
            (WorkloadKind.Job, V1Job job) => (
                job.Status?.Succeeded ?? 0,
                job.Spec?.Completions ?? Replicas,
                (job.Status?.Failed ?? 0) > 0,
                (job.Status?.Succeeded ?? 0) >= (job.Spec?.Completions ?? Replicas)),
            _ => throw new InvalidOperationException(
                $"Readiness rule '{Kind}' received unexpected Kubernetes object '{workload.Kind}'."),
        };

    private static bool IsCurrent(V1ObjectMeta? metadata, long? observedGeneration) =>
        metadata?.Generation is not long desiredGeneration
        || observedGeneration is long currentGeneration && currentGeneration >= desiredGeneration;

    private static (int Restarts, int? ExitCode) ReadContainers(IReadOnlyList<V1Pod> pods)
    {
        int restarts = 0;
        int? exitCode = null;
        for (int podIndex = 0; podIndex < pods.Count; podIndex++)
        {
            IList<V1ContainerStatus>? statuses = pods[podIndex].Status?.ContainerStatuses;
            if (statuses is null)
            {
                continue;
            }

            for (int statusIndex = 0; statusIndex < statuses.Count; statusIndex++)
            {
                V1ContainerStatus status = statuses[statusIndex];
                restarts += status.RestartCount;
                long? value = status.State?.Terminated?.ExitCode
                    ?? status.LastState?.Terminated?.ExitCode;
                if (value is not null)
                {
                    int candidate = checked((int)value.Value);
                    if (candidate is 64 or 70 || exitCode is null or 0)
                    {
                        exitCode = candidate;
                    }
                }
            }
        }

        return (restarts, exitCode);
    }

    private static bool PodsReady(IReadOnlyList<V1Pod> pods, int desired)
    {
        int ready = 0;
        for (int index = 0; index < pods.Count; index++)
        {
            IList<V1PodCondition>? conditions = pods[index].Status?.Conditions;
            if (conditions is null)
            {
                continue;
            }

            for (int conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
            {
                V1PodCondition condition = conditions[conditionIndex];
                if (string.Equals(condition.Type, "Ready", StringComparison.Ordinal)
                    && string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase))
                {
                    ready++;
                    break;
                }
            }
        }

        return ready >= desired;
    }

    private static bool DeploymentFailed(IList<V1DeploymentCondition>? conditions)
    {
        if (conditions is null)
        {
            return false;
        }

        for (int index = 0; index < conditions.Count; index++)
        {
            V1DeploymentCondition condition = conditions[index];
            if ((string.Equals(condition.Type, "ReplicaFailure", StringComparison.Ordinal)
                    && string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(condition.Type, "Progressing", StringComparison.Ordinal)
                    && string.Equals(condition.Status, "False", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(condition.Reason, "ProgressDeadlineExceeded", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StatefulSetFailed(IList<V1StatefulSetCondition>? conditions)
    {
        if (conditions is null)
        {
            return false;
        }

        for (int index = 0; index < conditions.Count; index++)
        {
            V1StatefulSetCondition condition = conditions[index];
            if (string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(condition.Type, "Failed", StringComparison.Ordinal)
                    || string.Equals(condition.Type, "ReplicaFailure", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DaemonSetFailed(IList<V1DaemonSetCondition>? conditions)
    {
        if (conditions is null)
        {
            return false;
        }

        for (int index = 0; index < conditions.Count; index++)
        {
            V1DaemonSetCondition condition = conditions[index];
            if (string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(condition.Type, "Failed", StringComparison.Ordinal)
                    || string.Equals(condition.Type, "ReplicaFailure", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
