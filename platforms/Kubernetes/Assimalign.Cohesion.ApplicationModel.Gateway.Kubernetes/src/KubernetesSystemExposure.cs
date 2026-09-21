namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>Controls external access to the system control-plane Service.</summary>
public enum KubernetesSystemExposure
{
    /// <summary>Publish only the in-cluster ClusterIP Service.</summary>
    None,
    /// <summary>Publish an additional LoadBalancer Service.</summary>
    LoadBalancer,
    /// <summary>Publish an Ingress using the configured host and class.</summary>
    Ingress,
}
