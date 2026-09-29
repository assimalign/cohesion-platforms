using System;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal static class KubernetesObjectTypes
{
    internal static void Restore(IKubernetesObject<V1ObjectMeta> resource, string? apiVersion = null, string? kind = null)
    {
        (string api, string type) = resource switch
        {
            V1ConfigMap => ("v1", "ConfigMap"),
            V1Secret => ("v1", "Secret"),
            V1Service => ("v1", "Service"),
            V1PersistentVolumeClaim => ("v1", "PersistentVolumeClaim"),
            V1Deployment => ("apps/v1", "Deployment"),
            V1StatefulSet => ("apps/v1", "StatefulSet"),
            V1DaemonSet => ("apps/v1", "DaemonSet"),
            V1Job => ("batch/v1", "Job"),
            _ => throw new NotSupportedException($"Unsupported Kubernetes object type '{resource.GetType().Name}'."),
        };
        if (string.IsNullOrWhiteSpace(resource.ApiVersion))
        {
            resource.ApiVersion = string.IsNullOrWhiteSpace(apiVersion) ? api : apiVersion;
        }

        if (string.IsNullOrWhiteSpace(resource.Kind))
        {
            resource.Kind = string.IsNullOrWhiteSpace(kind) ? type : kind;
        }
    }
}
