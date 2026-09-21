using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>Creates digest-verifying OCI image stores.</summary>
public static class OciImageStores
{
    /// <summary>Creates or opens a content-addressed image store.</summary>
    /// <param name="rootPath">The store root directory.</param>
    /// <returns>The image store.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> is <see langword="null"/>.</exception>
    public static IOciImageStore Create(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return new OciImageStore(rootPath);
    }
}
