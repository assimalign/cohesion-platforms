using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class EmbeddedOciRegistryTests
{
    [Fact(DisplayName = "Cohesion Test [Containers] - Embedded registry: Should answer the v2 capability endpoint")]
    public async Task GetAsync_OnV2Endpoint_ShouldReturnDistributionCapability()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        await IngestAsync(fixture);
        await using IEmbeddedOciRegistry registry = EmbeddedOciRegistries.Create(fixture.StorePath);
        await registry.StartAsync(CancellationToken.None);
        using HttpClient client = CreateClient(registry);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/v2/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        DistributionVersion(response).ShouldBe("registry/2.0");
        (await response.Content.ReadAsStringAsync()).ShouldBe("{}");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Embedded registry: Should serve manifest and blob GET and HEAD by digest")]
    public async Task SendAsync_OnManifestAndBlobRoutes_ShouldReturnVerifiedContent()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        await IngestAsync(fixture);
        await using IEmbeddedOciRegistry registry = EmbeddedOciRegistries.Create(fixture.StorePath);
        await registry.StartAsync(CancellationToken.None);
        using HttpClient client = CreateClient(registry);
        string manifestPath = Route(fixture, "manifests", fixture.ManifestDigest);
        string blobPath = Route(fixture, "blobs", fixture.LayerDigest);

        // Act
        using HttpResponseMessage manifestGet = await client.GetAsync(manifestPath);
        using HttpResponseMessage manifestHead = await SendHeadAsync(client, manifestPath);
        using HttpResponseMessage blobGet = await client.GetAsync(blobPath);
        using HttpResponseMessage blobHead = await SendHeadAsync(client, blobPath);

        // Assert
        await AssertContentAsync(
            manifestGet,
            "application/vnd.oci.image.manifest.v1+json",
            fixture.ManifestDigest,
            fixture.ManifestBytes,
            head: false);
        await AssertContentAsync(
            manifestHead,
            "application/vnd.oci.image.manifest.v1+json",
            fixture.ManifestDigest,
            fixture.ManifestBytes,
            head: true);
        await AssertContentAsync(
            blobGet,
            "application/octet-stream",
            fixture.LayerDigest,
            fixture.LayerBytes,
            head: false);
        await AssertContentAsync(
            blobHead,
            "application/octet-stream",
            fixture.LayerDigest,
            fixture.LayerBytes,
            head: true);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Embedded registry: Should not expose content through another repository")]
    public async Task GetAsync_OnWrongRepositoryOrDigest_ShouldReturnNotFound()
    {
        // Arrange
        using var fixture = new OciImageFixture();
        await IngestAsync(fixture);
        await using IEmbeddedOciRegistry registry = EmbeddedOciRegistries.Create(fixture.StorePath);
        await registry.StartAsync(CancellationToken.None);
        using HttpClient client = CreateClient(registry);
        string unknownDigest = "sha256:" + new string('f', 64);

        // Act
        using HttpResponseMessage wrongManifest = await client.GetAsync(
            $"/v2/another/team/api/manifests/{Uri.EscapeDataString(fixture.ManifestDigest)}");
        using HttpResponseMessage wrongBlob = await client.GetAsync(
            $"/v2/another/team/api/blobs/{Uri.EscapeDataString(fixture.LayerDigest)}");
        using HttpResponseMessage unknownBlob = await client.GetAsync(
            Route(fixture, "blobs", unknownDigest));

        // Assert
        wrongManifest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        wrongBlob.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownBlob.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        DistributionVersion(wrongManifest).ShouldBe("registry/2.0");
        DistributionVersion(wrongBlob).ShouldBe("registry/2.0");
        DistributionVersion(unknownBlob).ShouldBe("registry/2.0");
    }

    private static async Task IngestAsync(OciImageFixture fixture)
    {
        IOciImageStore store = OciImageStores.Create(fixture.StorePath);
        await store.IngestAsync(
            fixture.Repository,
            fixture.ManifestDigest,
            fixture.ArchivePath,
            CancellationToken.None);
    }

    private static HttpClient CreateClient(IEmbeddedOciRegistry registry) => new()
    {
        BaseAddress = registry.Endpoint,
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static string Route(OciImageFixture fixture, string kind, string digest) =>
        $"/v2/{fixture.Repository}/{kind}/{Uri.EscapeDataString(digest)}";

    private static async Task<HttpResponseMessage> SendHeadAsync(
        HttpClient client,
        string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        return await client.SendAsync(request);
    }

    private static async Task AssertContentAsync(
        HttpResponseMessage response,
        string mediaType,
        string digest,
        byte[] expected,
        bool head)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        DistributionVersion(response).ShouldBe("registry/2.0");
        response.Headers.GetValues("Docker-Content-Digest").Single().ShouldBe(digest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentLength.ShouldBe(expected.LongLength);
        byte[] content = await response.Content.ReadAsByteArrayAsync();
        if (head)
        {
            content.ShouldBeEmpty();
        }
        else
        {
            content.ShouldBe(expected);
        }
    }

    private static string DistributionVersion(HttpResponseMessage response) =>
        response.Headers.GetValues("Docker-Distribution-Api-Version").Single();
}
