using System;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Docker;
using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Configures how the Docker platform runs a resource.
/// </summary>
public static class DockerResourceDefinitionExtensions
{
    extension(IApplicationResourceDefinition resource)
    {
        /// <summary>
        /// Sets how many instances of the resource the Docker platform runs.
        /// </summary>
        /// <remarks>
        /// The count is the resource's <c>replicas</c> extension on the <c>docker</c> platform: the
        /// platform runs that many containers of the resource, each an instance of its own that
        /// publishes to the gateway, and all of them answer at the resource's name on the network.
        /// Without it the platform runs one. Another platform never sees it. Declaring it again
        /// replaces the count.
        /// </remarks>
        /// <param name="replicas">The number of instances, at least 1.</param>
        /// <returns>The same definition for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="replicas"/> is less than 1.</exception>
        public IApplicationResourceDefinition WithDockerReplicas(int replicas)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);

            using JsonDocument value = JsonDocument.Parse(replicas.ToString(CultureInfo.InvariantCulture));
            return resource.WithExtension(DockerProtocol.PlatformName, DockerWorkload.ReplicasExtension, value.RootElement);
        }
    }
}
