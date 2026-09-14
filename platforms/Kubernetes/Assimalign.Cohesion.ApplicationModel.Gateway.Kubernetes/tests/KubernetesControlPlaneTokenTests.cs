using System;
using System.Buffers.Text;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesControlPlaneTokenTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Trust: Upstream loopback server accepts the gateway token and refuses wrong audience or expiry")]
    public async Task IssueDeveloperTokenAsync_OnUpstreamLoopbackServer_ShouldEnforceAudienceAndExpiry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var keys = new TestGatewayTrustKeys();
        var clock = new MutableTimeProvider();
        var options = new KubernetesGatewayOptions
        {
            TrustKeyRepository = keys,
            TimeProvider = clock,
            DeveloperTokenLifetime = TimeSpan.FromMinutes(1),
        };
        var gateway = new KubernetesGateway(options);
        IApplicationModel model = KubernetesSystemInstallationTests.Model();
        IApplicationTrustGateway trust = gateway;
        string token = await trust.IssueDeveloperTokenAsync(model, "developer", timeout.Token);
        IApplicationGatewayControlPlane server = GatewayControlPlane.CreateFactory(options => options.TimeProvider = clock).Create(model.Name);
        using var client = new HttpClient();
        try
        {
            await server.StartAsync(new Uri("http://127.0.0.1:0"), model, new InMemoryResourceStateManager(), trust, timeout.Token);
            await server.PublishAsync(ApplicationExportDocument.Create(model, "1", trustKey: trust.GetTrustedIssuers(model.Name)[0].PublicKey), timeout.Token);
            (await SendAsync(client, server.Address, token, timeout.Token)).ShouldBe(HttpStatusCode.OK);

            // Re-sign only the audience-mutated test credential with the same fixture key, so
            // this checks audience validation rather than merely breaking a JWT signature.
            string[] pieces = token.Split('.');
            JsonNode payload = JsonNode.Parse(Base64Url.DecodeFromChars(pieces[1]))!;
            payload["aud"] = "wrong-audience";
            pieces[1] = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            using ECDsa key = await keys.LoadOrCreateAsync(model.Name, gateway.Name, timeout.Token);
            pieces[2] = Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(pieces[0] + "." + pieces[1]),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            (await SendAsync(client, server.Address, string.Join('.', pieces), timeout.Token)).ShouldBe(HttpStatusCode.Forbidden);
            // The upstream verifier allows five minutes of clock skew after expiration.
            clock.Now += TimeSpan.FromMinutes(10);
            (await SendAsync(client, server.Address, token, timeout.Token)).ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, Uri address, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, "/cohesion/v1/application"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
