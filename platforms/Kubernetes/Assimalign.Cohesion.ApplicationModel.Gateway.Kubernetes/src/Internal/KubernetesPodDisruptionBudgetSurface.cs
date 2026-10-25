using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The <c>kubernetes/pod-disruption-budget</c> platform surface: how many of a resource's pods a voluntary disruption,
/// such as a node drain, must leave running, declared with <c>ConfigurePodDisruptionBudget</c>.
/// </summary>
/// <remarks>The value states exactly one of <c>minAvailable</c> and <c>maxUnavailable</c>, as a pod count.</remarks>
internal static class KubernetesPodDisruptionBudgetSurface
{
    public static readonly ResourceExtensionKey Key = "kubernetes/pod-disruption-budget";

    public const string Schema = """
        {"type":"object","properties":{
        "minAvailable":{"type":["integer","null"]},
        "maxUnavailable":{"type":["integer","null"]}}}
        """;

    /// <summary>The budget in force for one compile: exactly one member is set.</summary>
    public sealed record Settings(int? MinAvailable, int? MaxUnavailable);

    // The surface a plan publishes, or null for a resource that declares no budget.
    public static ResourcePlatformSurface? Create(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Extensions.TryGetValue(Key.Value, out JsonElement extension))
        {
            return null;
        }

        if (plan.Workload.Kind is not (WorkloadKind.Deployment or WorkloadKind.StatefulSet))
        {
            throw new InvalidOperationException(
                $"Resource '{plan.Resource}' declares a pod disruption budget, but its workload kind '{plan.Workload.Kind}' is not replicated.");
        }

        try
        {
            return ResourcePlatformSurface.Create(Key, Schema, extension, Validate);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Resource '{plan.Resource}' carries invalid '{Key}' extension data: {exception.Message}", exception);
        }
    }

    public static Settings? Resolve(ResourcePlan plan, ResourceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (Create(plan) is not ResourcePlatformSurface surface)
        {
            return null;
        }

        return Read(surface.Apply(inputs.PlatformOverrides.TryGetValue(Key, out JsonElement patch) ? patch : null));
    }

    public static Settings Read(JsonElement value) => new(ReadOptional(value, "minAvailable"), ReadOptional(value, "maxUnavailable"));

    public static V1PodDisruptionBudget Compile(Settings settings, string namespaceName, string resource, string planHash, string owner) => new()
    {
        ApiVersion = $"{V1PodDisruptionBudget.KubeGroup}/{V1PodDisruptionBudget.KubeApiVersion}",
        Kind = V1PodDisruptionBudget.KubeKind,
        Metadata = KubernetesMetadata.CreateObjectMeta(resource, namespaceName, resource, planHash, owner),
        Spec = new V1PodDisruptionBudgetSpec
        {
            Selector = new V1LabelSelector
            {
                MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal) { [KubernetesMetadata.ResourceLabel] = resource },
            },
            MinAvailable = settings.MinAvailable is int minimum ? (IntstrIntOrString)minimum : null,
            MaxUnavailable = settings.MaxUnavailable is int maximum ? (IntstrIntOrString)maximum : null,
        },
    };

    public static JsonElement Observe(V1PodDisruptionBudget budget)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("currentHealthy", budget.Status?.CurrentHealthy ?? 0);
            writer.WriteNumber("desiredHealthy", budget.Status?.DesiredHealthy ?? 0);
            writer.WriteNumber("disruptionsAllowed", budget.Status?.DisruptionsAllowed ?? 0);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    // Kubernetes accepts exactly one of the two, as a pod count of zero or more.
    private static string? Validate(JsonElement value)
    {
        Settings settings = Read(value);
        if ((settings.MinAvailable is null) == (settings.MaxUnavailable is null))
        {
            return "A pod disruption budget states exactly one of minAvailable and maxUnavailable.";
        }

        return settings.MinAvailable is < 0 || settings.MaxUnavailable is < 0 ? "A pod disruption budget count must not be negative." : null;
    }

    private static int? ReadOptional(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement member) && member.ValueKind == JsonValueKind.Number
            ? member.GetInt32()
            : null;
}
