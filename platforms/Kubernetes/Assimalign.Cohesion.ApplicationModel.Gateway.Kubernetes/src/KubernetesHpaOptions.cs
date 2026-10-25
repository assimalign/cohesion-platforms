namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The horizontal pod autoscaler a Kubernetes gateway creates for one resource, declared in the gateway's
/// <c>Program.cs</c> with <c>ConfigureHpa</c>.
/// </summary>
/// <remarks>
/// <see cref="MinReplicas"/> and <see cref="MaxReplicas"/> become the resource's platform-neutral scale bounds
/// (<see cref="ScaleBounds"/>), so they survive a change of platform; the targets are Kubernetes extension data. The
/// gateway's management API publishes the autoscaler as the <c>kubernetes/hpa</c> platform surface, where an
/// operator's override lasts until the resource's next deployment.
/// </remarks>
public sealed class KubernetesHpaOptions
{
    /// <summary>Gets or sets the fewest replicas; when unset, the resource's existing scale minimum, or one.</summary>
    public int? MinReplicas { get; set; }

    /// <summary>Gets or sets the most replicas; required unless the resource already has scale bounds.</summary>
    public int? MaxReplicas { get; set; }

    /// <summary>
    /// Gets or sets the average CPU utilization the autoscaler holds, as a percentage of the container's CPU request.
    /// When neither target is set, the autoscaler holds 80 percent CPU.
    /// </summary>
    public int? TargetCpuUtilizationPercentage { get; set; }

    /// <summary>Gets or sets the average memory utilization the autoscaler holds, as a percentage of the container's memory request.</summary>
    public int? TargetMemoryUtilizationPercentage { get; set; }

    /// <summary>Gets or sets how many seconds of lower recommendations the autoscaler waits for before it scales down.</summary>
    public int? ScaleDownStabilizationWindowSeconds { get; set; }
}
