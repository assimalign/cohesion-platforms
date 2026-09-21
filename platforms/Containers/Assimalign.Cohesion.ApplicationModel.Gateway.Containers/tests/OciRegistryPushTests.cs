using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class OciRegistryPushTests
{
    [Fact(DisplayName = "Cohesion Test [Containers] - Push: Upload verified closure and skip existing blobs on repeat")]
    public async Task PushAsync_OnRepeat_ShouldUploadMissingBlobsOnly()
    {
        using var image = new OciImageFixture();
        await OciImageStores.Create(image.StorePath).IngestAsync(image.Repository, image.ManifestDigest, image.ArchivePath);
        using var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var client = new OciRegistryPushClient(http);
        await client.PushAsync(image.StorePath, image.Repository, image.ManifestDigest, new Uri("http://localhost:5001"), CancellationToken.None);
        registry.Blobs.Count.ShouldBe(2);
        registry.Uploads.ShouldBe(2);
        registry.Manifest.ShouldBe(image.ManifestBytes);
        registry.ManifestPath.ShouldEndWith("/manifests/" + image.ManifestDigest);
        await client.PushAsync(image.StorePath, image.Repository, image.ManifestDigest, new Uri("http://localhost:5001"), CancellationToken.None);
        registry.Uploads.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Push: Reject wrong digest acknowledgement or cross-authority upload")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PushAsync_OnInvalidRegistryResponse_ShouldReject(bool wrongDigest, bool foreignLocation)
    {
        using var image = new OciImageFixture();
        await OciImageStores.Create(image.StorePath).IngestAsync(image.Repository, image.ManifestDigest, image.ArchivePath);
        using var registry = new RegistryHandler { WrongDigest = wrongDigest, ForeignLocation = foreignLocation };
        using var http = new HttpClient(registry);
        await Should.ThrowAsync<InvalidDataException>(() => new OciRegistryPushClient(http).PushAsync(
            image.StorePath, image.Repository, image.ManifestDigest, new Uri("http://localhost:5001"), CancellationToken.None));
        registry.Manifest.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Push: Reject content changed after ingestion before network access")]
    public async Task PushAsync_OnCorruptStore_ShouldReject()
    {
        using var image = new OciImageFixture();
        await OciImageStores.Create(image.StorePath).IngestAsync(image.Repository, image.ManifestDigest, image.ArchivePath);
        File.WriteAllText(Path.Combine(image.StorePath, "blobs", "sha256", image.ConfigDigest[7..]), "corrupt");
        using var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        await Should.ThrowAsync<InvalidDataException>(() => new OciRegistryPushClient(http).PushAsync(
            image.StorePath, image.Repository, image.ManifestDigest, new Uri("http://localhost:5001"), CancellationToken.None));
        registry.Uploads.ShouldBe(0);
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        public HashSet<string> Blobs { get; } = [];
        public int Uploads { get; private set; }
        public byte[]? Manifest { get; private set; }
        public string? ManifestPath { get; private set; }
        public bool WrongDigest { get; init; }
        public bool ForeignLocation { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head)
            {
                string digest = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
                return new HttpResponseMessage(Blobs.Contains(digest) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Post)
            {
                Uploads++;
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri(ForeignLocation ? "http://other.test/upload" : "/upload?_state=opaque", UriKind.RelativeOrAbsolute);
                return response;
            }
            request.Method.ShouldBe(HttpMethod.Put);
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            string actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (uri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
            {
                Manifest = bytes;
                ManifestPath = uri.AbsolutePath;
                uri.AbsolutePath.ShouldEndWith(actual);
            }
            else
            {
                uri.Query.ShouldContain("_state=opaque&digest=");
                Uri.UnescapeDataString(uri.Query).ShouldEndWith(actual);
                Blobs.Add(actual);
            }
            var result = new HttpResponseMessage(HttpStatusCode.Created);
            result.Headers.Add("Docker-Content-Digest", WrongDigest ? "sha256:" + new string('f', 64) : actual);
            return result;
        }
    }
}
