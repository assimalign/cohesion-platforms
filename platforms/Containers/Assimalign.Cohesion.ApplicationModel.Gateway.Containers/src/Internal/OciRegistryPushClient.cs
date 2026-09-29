using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class OciRegistryPushClient(HttpClient http)
{
    public async Task PushAsync(string storePath, string repository, string digest, Uri registry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!registry.IsAbsoluteUri || registry.Scheme is not ("http" or "https") ||
            registry.AbsolutePath != "/" || registry.Query.Length != 0 || registry.Fragment.Length != 0 || registry.UserInfo.Length != 0)
        {
            throw new ArgumentException("Registry must be an HTTP(S) authority.", nameof(registry));
        }

        var store = new OciImageStore(storePath);
        if (!store.TryGetManifest(repository, digest, out StoredImageContent manifest))
        {
            throw new InvalidDataException($"Verified manifest '{repository}@{digest}' is absent from the store.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(manifest.Path, cancellationToken).ConfigureAwait(false);
        RequireDigest(SHA256.HashData(bytes), digest);
        using JsonDocument document = JsonDocument.Parse(bytes);
        var blobs = new List<JsonElement> { document.RootElement.GetProperty("config") };
        blobs.AddRange(document.RootElement.GetProperty("layers").EnumerateArray());
        string prefix = $"v2/{repository}/";
        foreach (JsonElement descriptor in blobs)
        {
            string blobDigest = descriptor.GetProperty("digest").GetString()!;
            if (!store.TryGetBlob(repository, blobDigest, out StoredImageContent blob))
            {
                throw new InvalidDataException($"Verified blob '{blobDigest}' is absent from the store.");
            }

            await using FileStream stream = File.OpenRead(blob.Path);
            RequireDigest(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false), blobDigest);
            if (stream.Length != descriptor.GetProperty("size").GetInt64())
            {
                throw new InvalidDataException($"Stored blob '{blobDigest}' has an incorrect size.");
            }

            stream.Position = 0;
            using var head = new HttpRequestMessage(HttpMethod.Head, new Uri(registry, prefix + "blobs/" + blobDigest));
            using HttpResponseMessage existing = await http.SendAsync(head, cancellationToken).ConfigureAwait(false);
            if (existing.StatusCode == HttpStatusCode.OK)
            {
                continue;
            }

            RequireStatus(existing, HttpStatusCode.NotFound);
            using HttpResponseMessage upload = await http.PostAsync(new Uri(registry, prefix + "blobs/uploads/"), null, cancellationToken).ConfigureAwait(false);
            RequireStatus(upload, HttpStatusCode.Accepted);
            Uri location = new(registry, upload.Headers.Location ?? throw new InvalidDataException("Registry upload response has no Location."));
            if (location.GetLeftPart(UriPartial.Authority) != registry.GetLeftPart(UriPartial.Authority))
            {
                throw new InvalidDataException("Registry redirected a blob upload outside its authority.");
            }

            var destination = new UriBuilder(location);
            destination.Query = location.Query.TrimStart('?') + (location.Query.Length == 0 ? "" : "&") + "digest=" + Uri.EscapeDataString(blobDigest);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage pushed = await http.PutAsync(destination.Uri, content, cancellationToken).ConfigureAwait(false);
            RequireStatus(pushed, HttpStatusCode.Created);
            RequireAcknowledgement(pushed, blobDigest);
        }
        using var manifestContent = new ByteArrayContent(bytes);
        manifestContent.Headers.ContentType = new MediaTypeHeaderValue(manifest.MediaType);
        using HttpResponseMessage result = await http.PutAsync(new Uri(registry, prefix + "manifests/" + digest), manifestContent, cancellationToken).ConfigureAwait(false);
        RequireStatus(result, HttpStatusCode.Created);
        RequireAcknowledgement(result, digest);
    }

    private static void RequireDigest(byte[] hash, string expected)
    {
        if ("sha256:" + Convert.ToHexStringLower(hash) != expected)
        {
            throw new InvalidDataException($"Store content does not match '{expected}'.");
        }
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new HttpRequestException($"Registry returned {(int)response.StatusCode}; expected {(int)expected}.", null, response.StatusCode);
        }
    }

    private static void RequireAcknowledgement(HttpResponseMessage response, string digest)
    {
        if (!response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? values) || values.SingleOrDefault() != digest)
        {
            throw new InvalidDataException($"Registry did not acknowledge expected digest '{digest}'.");
        }
    }
}
