namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The pod disruption budget a Kubernetes gateway creates for one resource, declared in the gateway's
/// <c>Program.cs</c> with <c>ConfigurePodDisruptionBudget</c>: how many of its pods a voluntary disruption, such as
/// a node drain, must leave running.
/// </summary>
/// <remarks>
/// Set exactly one of <see cref="MinAvailable"/> and <see cref="MaxUnavailable"/>. The gateway's management API
/// publishes the budget as the <c>kubernetes/pod-disruption-budget</c> platform surface.
/// </remarks>
public sealed class KubernetesPodDisruptionBudgetOptions
{
    /// <summary>Gets or sets the fewest pods that must stay available during a voluntary disruption.</summary>
    public int? MinAvailable { get; set; }

    /// <summary>Gets or sets the most pods a voluntary disruption may make unavailable at once.</summary>
    public int? MaxUnavailable { get; set; }
}
