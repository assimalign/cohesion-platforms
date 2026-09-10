using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesPlanCompilation
{
    public KubernetesPlanCompilation(
        string namespaceName,
        string planHash,
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects,
        KubernetesReadinessRule readiness,
        IReadOnlyList<ResourceEndpoint> endpoints,
        IReadOnlyList<string> warnings)
    {
        NamespaceName = namespaceName;
        PlanHash = planHash;
        Objects = Copy(objects);
        Readiness = readiness;
        Endpoints = Copy(endpoints);
        Warnings = Copy(warnings);
    }

    public string NamespaceName { get; }

    public string PlanHash { get; }

    public IReadOnlyList<IKubernetesObject<V1ObjectMeta>> Objects { get; }

    public KubernetesReadinessRule Readiness { get; }

    public IReadOnlyList<ResourceEndpoint> Endpoints { get; }

    public IReadOnlyList<string> Warnings { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }
}
