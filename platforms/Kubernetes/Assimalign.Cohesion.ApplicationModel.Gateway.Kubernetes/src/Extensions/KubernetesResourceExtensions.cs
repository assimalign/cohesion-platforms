using System;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Declares the Kubernetes objects that accompany a resource's workload, in the gateway's <c>Program.cs</c>: platform
/// code lives only there, so switching platform rewrites gateway code, never resource code.
/// </summary>
/// <remarks>
/// The members are generic over the descriptor type, so an area descriptor such as <c>IWebResourceDescriptor</c> keeps
/// its type and the area's own verbs still chain after them. They store data under platform-qualified keys
/// (<c>kubernetes/hpa</c>, <c>kubernetes/pod-disruption-budget</c>), never callbacks, so a model document carries them
/// and every gateway other than Kubernetes ignores them.
/// </remarks>
public static class KubernetesResourceExtensions
{
    extension<TDescriptor>(TDescriptor descriptor)
        where TDescriptor : IApplicationResourceDescriptor
    {
        /// <summary>Autoscales the resource's workload with a Kubernetes horizontal pod autoscaler.</summary>
        /// <param name="configure">Sets the replica bounds and the utilization targets.</param>
        /// <returns>The descriptor, for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Each call starts from what earlier calls and <c>ScaleBetween</c> declared, so calls compose. The replica
        /// bounds are stored as the resource's neutral <see cref="ScaleBounds"/>; the targets as <c>kubernetes/hpa</c>
        /// extension data. The gateway leaves the workload's replica count to the autoscaler.
        /// </para>
        /// <para>
        /// Changing the bounds or the targets in code updates the autoscaler without restarting the resource's pods. Like
        /// any change to the resource's deployment, it starts a new generation, which discards the resource's management
        /// overrides.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">The descriptor or <paramref name="configure"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// No maximum is declared, a bound or target is out of range, the descriptor belongs to a built model, or its
        /// resource is not one this application realizes.
        /// </exception>
        public TDescriptor ConfigureHpa(Action<KubernetesHpaOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(configure);
            var options = new KubernetesHpaOptions();
            if (descriptor.TryGetScale(out ScaleBounds? scale))
            {
                options.MinReplicas = scale.Minimum;
                options.MaxReplicas = scale.Maximum;
            }

            if (descriptor.TryGetExtension(KubernetesHpaSurface.Key, out JsonElement existing))
            {
                options.TargetCpuUtilizationPercentage = ReadOptional(existing, "targetCpuUtilizationPercentage");
                options.TargetMemoryUtilizationPercentage = ReadOptional(existing, "targetMemoryUtilizationPercentage");
                options.ScaleDownStabilizationWindowSeconds = ReadOptional(existing, "scaleDownStabilizationWindowSeconds");
            }

            configure(options);
            int maximum = options.MaxReplicas
                ?? throw new InvalidOperationException(
                    $"ConfigureHpa for resource '{descriptor.Resource.Name}' needs MaxReplicas: the resource has no scale bounds to start from.");
            int minimum = options.MinReplicas ?? 1;
            if (minimum < 1 || maximum < minimum)
            {
                throw new InvalidOperationException(
                    $"ConfigureHpa for resource '{descriptor.Resource.Name}' declares replicas {minimum} to {maximum}; " +
                    "the minimum must be at least 1 and the maximum at least the minimum.");
            }

            if (options.TargetCpuUtilizationPercentage is < 1 || options.TargetMemoryUtilizationPercentage is < 1 ||
                options.ScaleDownStabilizationWindowSeconds is < 0)
            {
                throw new InvalidOperationException(
                    $"ConfigureHpa for resource '{descriptor.Resource.Name}' declares a target below 1 percent or a negative stabilization window.");
            }

            descriptor.ScaleBetween(minimum, maximum);
            return descriptor.SetExtension(KubernetesHpaSurface.Key, WriteTargets(options));
        }

        /// <summary>Protects the resource's workload with a Kubernetes pod disruption budget.</summary>
        /// <param name="configure">Sets exactly one of the minimum available and the maximum unavailable pod count.</param>
        /// <returns>The descriptor, for chaining.</returns>
        /// <remarks>
        /// A later call replaces the budget. Changing it in code updates the budget without restarting the resource's pods.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The descriptor or <paramref name="configure"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Neither or both counts are set, a count is negative, the descriptor belongs to a built model, or its resource
        /// is not one this application realizes.
        /// </exception>
        public TDescriptor ConfigurePodDisruptionBudget(Action<KubernetesPodDisruptionBudgetOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(configure);
            var options = new KubernetesPodDisruptionBudgetOptions();
            configure(options);
            if ((options.MinAvailable is null) == (options.MaxUnavailable is null) ||
                options.MinAvailable is < 0 || options.MaxUnavailable is < 0)
            {
                throw new InvalidOperationException(
                    $"ConfigurePodDisruptionBudget for resource '{descriptor.Resource.Name}' must set exactly one of " +
                    "MinAvailable and MaxUnavailable, to zero or more.");
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (options.MinAvailable is int minimum)
                {
                    writer.WriteNumber("minAvailable", minimum);
                }
                else
                {
                    writer.WriteNumber("maxUnavailable", options.MaxUnavailable!.Value);
                }

                writer.WriteEndObject();
            }

            return descriptor.SetExtension(KubernetesPodDisruptionBudgetSurface.Key, Parse(stream.ToArray()));
        }
    }

    private static JsonElement WriteTargets(KubernetesHpaOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteOptional(writer, "targetCpuUtilizationPercentage", options.TargetCpuUtilizationPercentage);
            WriteOptional(writer, "targetMemoryUtilizationPercentage", options.TargetMemoryUtilizationPercentage);
            WriteOptional(writer, "scaleDownStabilizationWindowSeconds", options.ScaleDownStabilizationWindowSeconds);
            writer.WriteEndObject();
        }

        return Parse(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static int? ReadOptional(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement member) && member.ValueKind == JsonValueKind.Number
            ? member.GetInt32()
            : null;

    private static JsonElement Parse(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
