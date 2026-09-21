using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>Creates pull-only embedded OCI Distribution registries.</summary>
public static class EmbeddedOciRegistries
{
    /// <summary>
    /// Creates a registry backed by an existing OCI image-store directory.
    /// </summary>
    /// <param name="storePath">The image-store root created through <see cref="OciImageStores"/>.</param>
    /// <param name="port">The loopback port, or zero to allocate an ephemeral port.</param>
    /// <returns>The stopped registry. Call <see cref="IEmbeddedOciRegistry.StartAsync"/> before use.</returns>
    /// <exception cref="ArgumentException"><paramref name="storePath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="storePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside 0 through 65535.</exception>
    public static IEmbeddedOciRegistry Create(string storePath, int port = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return new EmbeddedOciRegistry(storePath, port);
    }
}
