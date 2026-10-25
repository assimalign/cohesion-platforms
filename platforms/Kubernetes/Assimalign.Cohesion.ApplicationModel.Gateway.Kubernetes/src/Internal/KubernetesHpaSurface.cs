using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The <c>kubernetes/hpa</c> platform surface: the horizontal pod autoscaler that realizes a resource's neutral scale
/// bounds, with the metric targets <c>ConfigureHpa</c> adds as extension data.
/// </summary>
/// <remarks>
/// The surface's value joins the plan's <see cref="WorkloadSpec.Scale"/> (the replica bounds) with the
/// <c>kubernetes/hpa</c> extension (the targets); an operator's override of either part lasts until the resource's
/// next deployment. Without a CPU or memory target the autoscaler holds 80 % CPU utilization.
/// </remarks>
internal static class KubernetesHpaSurface
{
    public const int DefaultCpuUtilization = 80;

    public static readonly ResourceExtensionKey Key = "kubernetes/hpa";

    public const string Schema = """
        {"type":"object","properties":{
        "minReplicas":{"type":"integer"},
        "maxReplicas":{"type":"integer"},
        "targetCpuUtilizationPercentage":{"type":["integer","null"]},
        "targetMemoryUtilizationPercentage":{"type":["integer","null"]},
        "scaleDownStabilizationWindowSeconds":{"type":["integer","null"]}},
        "required":["minReplicas","maxReplicas"]}
        """;

    // The extension data ConfigureHpa writes: only the targets, since the bounds are neutral and live in the plan.
    public const string ExtensionSchema = """
        {"type":"object","properties":{
        "targetCpuUtilizationPercentage":{"type":["integer","null"]},
        "targetMemoryUtilizationPercentage":{"type":["integer","null"]},
        "scaleDownStabilizationWindowSeconds":{"type":["integer","null"]}}}
        """;

    /// <summary>The settings in force for one compile.</summary>
    public sealed record Settings(int MinReplicas, int MaxReplicas, int? CpuUtilization, int? MemoryUtilization, int? ScaleDownWindowSeconds);

    // The surface a plan publishes, or null for a resource without scale bounds. A malformed extension fails here,
    // which the gateway reaches at Build() through its resource validation.
    public static ResourcePlatformSurface? Create(ResourcePlan plan, int? ceiling)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Workload.Scale is not ScaleBounds scale)
        {
            if (plan.Extensions.ContainsKey(Key.Value))
            {
                throw new InvalidOperationException(
                    $"Resource '{plan.Resource}' carries '{Key}' extension data but no scale bounds. Declare them with ConfigureHpa or ScaleBetween.");
            }

            return null;
        }

        JsonElement targets = default;
        if (plan.Extensions.TryGetValue(Key.Value, out JsonElement extension))
        {
            try
            {
                // The extension schema is closed, so a misspelt target fails instead of being ignored.
                targets = ResourcePlatformSurface.Create(Key, ExtensionSchema, extension).Value;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"Resource '{plan.Resource}' carries invalid '{Key}' extension data: {exception.Message}", exception);
            }
        }

        var settings = new Settings(
            scale.Minimum,
            scale.Maximum,
            ReadOptional(targets, "targetCpuUtilizationPercentage"),
            ReadOptional(targets, "targetMemoryUtilizationPercentage"),
            ReadOptional(targets, "scaleDownStabilizationWindowSeconds"));
        return ResourcePlatformSurface.Create(Key, Schema, Write(settings), value => Validate(value, ceiling));
    }

    // The settings code declares with the current generation's override applied, or null without scale bounds.
    public static Settings? Resolve(ResourcePlan plan, ResourceInputs inputs, int? ceiling)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (Create(plan, ceiling) is not ResourcePlatformSurface surface)
        {
            return null;
        }

        JsonElement value = surface.Apply(inputs.PlatformOverrides.TryGetValue(Key, out JsonElement patch) ? patch : null);
        return Read(value);
    }

    public static Settings Read(JsonElement value) => new(
        value.GetProperty("minReplicas").GetInt32(),
        value.GetProperty("maxReplicas").GetInt32(),
        ReadOptional(value, "targetCpuUtilizationPercentage"),
        ReadOptional(value, "targetMemoryUtilizationPercentage"),
        ReadOptional(value, "scaleDownStabilizationWindowSeconds"));

    public static V2HorizontalPodAutoscaler Compile(
        ResourcePlan plan,
        Settings settings,
        string namespaceName,
        string resource,
        string planHash,
        string owner)
    {
        var metrics = new List<V2MetricSpec>();
        int? cpu = settings.CpuUtilization ?? (settings.MemoryUtilization is null ? DefaultCpuUtilization : null);
        if (cpu is int cpuTarget)
        {
            metrics.Add(CreateMetric("cpu", cpuTarget));
        }

        if (settings.MemoryUtilization is int memoryTarget)
        {
            metrics.Add(CreateMetric("memory", memoryTarget));
        }

        return new V2HorizontalPodAutoscaler
        {
            ApiVersion = $"{V2HorizontalPodAutoscaler.KubeGroup}/{V2HorizontalPodAutoscaler.KubeApiVersion}",
            Kind = V2HorizontalPodAutoscaler.KubeKind,
            Metadata = KubernetesMetadata.CreateObjectMeta(resource, namespaceName, resource, planHash, owner),
            Spec = new V2HorizontalPodAutoscalerSpec
            {
                ScaleTargetRef = new V2CrossVersionObjectReference
                {
                    ApiVersion = "apps/v1",
                    Kind = plan.Workload.Kind is WorkloadKind.StatefulSet ? V1StatefulSet.KubeKind : V1Deployment.KubeKind,
                    Name = resource,
                },
                MinReplicas = settings.MinReplicas,
                MaxReplicas = settings.MaxReplicas,
                Metrics = metrics,
                Behavior = settings.ScaleDownWindowSeconds is int window
                    ? new V2HorizontalPodAutoscalerBehavior { ScaleDown = new V2HPAScalingRules { StabilizationWindowSeconds = window } }
                    : null,
            },
        };
    }

    // What the autoscaler reports, without its conditions' messages, which can echo cluster detail.
    public static JsonElement Observe(V2HorizontalPodAutoscaler autoscaler)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("currentReplicas", autoscaler.Status?.CurrentReplicas ?? 0);
            writer.WriteNumber("desiredReplicas", autoscaler.Status?.DesiredReplicas ?? 0);
            if (autoscaler.Status?.LastScaleTime is DateTime scaled)
            {
                writer.WriteString("lastScaleTime", scaled.ToUniversalTime());
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    // The rules the schema cannot state: at least one replica, a maximum no lower than the minimum and within the
    // manifest's maxReplicas, positive targets and a non-negative stabilization window.
    private static string? Validate(JsonElement value, int? ceiling)
    {
        Settings settings = Read(value);
        if (settings.MinReplicas < 1)
        {
            return "minReplicas must be at least 1.";
        }

        if (settings.MaxReplicas < settings.MinReplicas)
        {
            return "maxReplicas must be at least minReplicas.";
        }

        if (ceiling is int maximum && settings.MaxReplicas > maximum)
        {
            return $"maxReplicas must not exceed the manifest's maxReplicas of {maximum}.";
        }

        if (settings.CpuUtilization is < 1 || settings.MemoryUtilization is < 1)
        {
            return "A utilization target must be at least 1 percent.";
        }

        return settings.ScaleDownWindowSeconds is < 0 ? "scaleDownStabilizationWindowSeconds must not be negative." : null;
    }

    private static V2MetricSpec CreateMetric(string name, int utilization) => new()
    {
        Type = "Resource",
        Resource = new V2ResourceMetricSource
        {
            Name = name,
            Target = new V2MetricTarget { Type = "Utilization", AverageUtilization = utilization },
        },
    };

    private static JsonElement Write(Settings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("minReplicas", settings.MinReplicas);
            writer.WriteNumber("maxReplicas", settings.MaxReplicas);
            WriteOptional(writer, "targetCpuUtilizationPercentage", settings.CpuUtilization);
            WriteOptional(writer, "targetMemoryUtilizationPercentage", settings.MemoryUtilization);
            WriteOptional(writer, "scaleDownStabilizationWindowSeconds", settings.ScaleDownWindowSeconds);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static int? ReadOptional(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement member) && member.ValueKind == JsonValueKind.Number
            ? member.GetInt32()
            : null;
}
