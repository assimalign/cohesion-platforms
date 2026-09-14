using System;
using System.Collections.Generic;

using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal static class KubernetesMetadata
{
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";
    public const string ManagedByValue = "cohesion";
    public const string ResourceLabel = "cohesion.io/resource";
    public const string SystemLabel = "cohesion.io/system";
    public const string PlanHashAnnotation = "cohesion.io/plan-hash";
    public const string RuntimeInputRevisionAnnotation = "cohesion.io/runtime-input-revision";
    public const string WorkloadRevisionAnnotation = "cohesion.io/workload-revision";
    public const string JobSpecRevisionAnnotation = "cohesion.io/job-spec-revision";
    public const string VolumeClaimsRevisionAnnotation = "cohesion.io/volume-claims-revision";
    public const string OwnerAnnotation = "cohesion.io/owner";
    public const string ExportName = "cohesion-export";

    public static string NamespaceName(ApplicationName application) =>
        RequireDnsLabel(application.Value.ToLowerInvariant(), "application namespace");

    public static string ResourceName(ResourceName resource) =>
        RequireDnsLabel(resource.Value.ToLowerInvariant(), "resource name");

    public static string ChildName(string resource, string suffix)
    {
        string candidate = $"{resource}-{suffix}";
        return RequireDnsLabel(candidate, "compiled object name");
    }

    public static V1ObjectMeta CreateObjectMeta(
        string name,
        string namespaceName,
        string resource,
        string planHash,
        string owner) => new()
        {
            Name = name,
            NamespaceProperty = namespaceName,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ManagedByLabel] = ManagedByValue,
                [ResourceLabel] = resource,
            },
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PlanHashAnnotation] = planHash,
                [OwnerAnnotation] = owner,
            },
        };

    public static void RestoreRequiredMetadata(
        V1ObjectMeta metadata,
        string name,
        string namespaceName,
        string resource,
        string planHash,
        string owner)
    {
        metadata.Name = name;
        metadata.NamespaceProperty = namespaceName;
        metadata.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        metadata.Annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
        metadata.Labels[ManagedByLabel] = ManagedByValue;
        metadata.Labels[ResourceLabel] = resource;
        metadata.Annotations[PlanHashAnnotation] = planHash;
        metadata.Annotations[OwnerAnnotation] = owner;
    }

    public static string RequireDnsLabel(string value, string description)
    {
        if (value.Length is 0 or > 63)
        {
            throw new InvalidOperationException(
                $"The Kubernetes {description} '{value}' must contain between 1 and 63 characters.");
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool alphanumeric = character is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (alphanumeric)
            {
                continue;
            }

            if (character != '-' || index == 0 || index == value.Length - 1)
            {
                throw new InvalidOperationException(
                    $"The Kubernetes {description} '{value}' is not an RFC 1123 DNS label.");
            }
        }

        return value;
    }
}
