using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>Pushes verified store content to a Registry API v2 endpoint by digest.</summary>
public static class OciRegistryPush
{
    /// <summary>Uploads missing blobs and the unchanged manifest, verifying the acknowledged digest.</summary>
    /// <param name="storePath">The root created by OciImageStores.</param>
    /// <param name="repository">The image repository without a registry authority.</param>
    /// <param name="digest">The expected manifest SHA-256 digest.</param>
    /// <param name="registry">An HTTP(S) registry authority.</param>
    /// <param name="cancellationToken">Cancels verification and requests.</param>
    /// <returns>A task completing after the registry acknowledges the manifest digest.</returns>
    /// <exception cref="ArgumentException">The registry endpoint is invalid.</exception>
    /// <exception cref="System.IO.InvalidDataException">Store content or a registry acknowledgement is invalid.</exception>
    /// <exception cref="System.IO.IOException">Stored image content cannot be read.</exception>
    /// <exception cref="HttpRequestException">A registry request fails.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task PushAsync(string storePath, string repository, string digest, Uri registry,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        await new OciRegistryPushClient(http).PushAsync(storePath, repository, digest, registry, cancellationToken).ConfigureAwait(false);
    }
}
