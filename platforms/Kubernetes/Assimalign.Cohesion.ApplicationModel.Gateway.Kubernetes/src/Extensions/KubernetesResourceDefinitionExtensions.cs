using System;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;
using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Configures how the Kubernetes platform runs a resource.
/// </summary>
public static class KubernetesResourceDefinitionExtensions
{
    extension(IApplicationResourceDefinition resource)
    {
        /// <summary>
        /// Sets how many instances of the resource the Kubernetes platform runs.
        /// </summary>
        /// <remarks>
        /// The count is the resource's <c>replicas</c> extension on the <c>kubernetes</c> platform: the
        /// resource's Deployment runs that many pods, each an instance of its own that publishes to the
        /// gateway. Without it the platform runs one. Another platform never sees it. Declaring it again
        /// replaces the count.
        /// </remarks>
        /// <param name="replicas">The number of instances, at least 1.</param>
        /// <returns>The same definition for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="replicas"/> is less than 1.</exception>
        public IApplicationResourceDefinition WithReplicas(int replicas)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);

            using JsonDocument value = JsonDocument.Parse(replicas.ToString(CultureInfo.InvariantCulture));
            return resource.WithExtension(KubernetesProtocol.PlatformName, KubernetesWorkload.ReplicasExtension, value.RootElement);
        }
    }
}
