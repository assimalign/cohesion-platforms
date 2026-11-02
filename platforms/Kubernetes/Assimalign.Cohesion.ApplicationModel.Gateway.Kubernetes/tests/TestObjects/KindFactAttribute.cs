using System;

using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// A test that runs against a kind cluster: skipped unless <c>COHESION_KIND_CONTEXT</c> names the
/// cluster's kubeconfig context, as <c>scripts/New-CohesionKindCluster.ps1</c> prints it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KindFactAttribute : FactAttribute
{
    public KindFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KindCluster.ContextVariable)))
        {
            Skip = $"Set {KindCluster.ContextVariable} to a kind cluster's kubeconfig context to run it.";
        }
    }
}
