using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public partial class DockerGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Control plane: Should publish metadata beside export and authenticate the real listener")]
    public async Task StartAsync_OnHostControlPlane_ShouldAuthenticateDiscoveryAndRejectInvalidTokens()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-docker-control-plane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engineServer = new FakeDockerEngine();
        var options = new DockerGatewayOptions { ExportDirectory = root };
        DockerGatewayCommandLine.Apply(options, ["--control-plane-bind=127.0.0.1:0"]);
        GatewayControlPlane.Configure(options, GatewayRunMode.Run);
        options.Controllers.Add(new CapturingController());
        var gateway = new DockerGateway(options, () => new DockerEngineClient(engineServer.Endpoint));
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), ["--environment", "Local"]);
        builder.AddResource(CreateManifest($"registry.example/worker@sha256:{new string('a', 64)}"));
        builder.UseGateway(gateway);
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        using var client = new HttpClient();
        using var signedTokens = new DockerControlPlaneToken();

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);
            string metadataPath = Path.Combine(root, "appa", "control-plane.json");
            using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, cancellation.Token));
            var address = new Uri(metadata.RootElement.GetProperty("url").GetString()!, UriKind.Absolute);
            string developerToken = await ((IApplicationTrustGateway)gateway).IssueDeveloperTokenAsync(
                model, "developer", cancellation.Token);
            IApplicationBuilder peerBuilder = Application.CreateBuilder(ApplicationName.Parse("peer"), []);
            ResourceManifest peerManifest = CreateManifest($"registry.example/worker@sha256:{new string('a', 64)}")
                with
            { Application = "peer" };
            peerBuilder.AddResource(peerManifest);
            peerBuilder.UseGateway(new DockerGateway());
            IApplicationModel peer = peerBuilder.Build().Model;
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(
                model, "peer", ApplicationExportDocument.Create(peer, "1", trustKey: signedTokens.PublicKey),
                cancellation.Token);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Uri endpoint = new(address, "/cohesion/v1/application");
            using HttpResponseMessage anonymous = await client.GetAsync(endpoint, cancellation.Token);
            using HttpResponseMessage developer = await GetAuthenticatedAsync(client, endpoint, developerToken, cancellation.Token);
            using HttpResponseMessage peerValid = await GetAuthenticatedAsync(client, endpoint,
                signedTokens.Create("cohesion-export", now.AddMinutes(-1), now.AddHours(1)), cancellation.Token);
            using HttpResponseMessage wrongAudience = await GetAuthenticatedAsync(client, endpoint,
                signedTokens.Create("wrong-audience", now.AddMinutes(-1), now.AddHours(1)), cancellation.Token);
            using HttpResponseMessage expired = await GetAuthenticatedAsync(client, endpoint,
                signedTokens.Create("cohesion-export", now.AddHours(-2), now.AddHours(-1)), cancellation.Token);

            // Assert
            address.IdnHost.ShouldBe("127.0.0.1");
            address.Port.ShouldBeGreaterThan(0);
            metadata.RootElement.GetProperty("trustKey").ValueKind.ShouldBe(JsonValueKind.Object);
            File.Exists(Path.Combine(root, "appa", "export.json")).ShouldBeTrue();
            anonymous.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
            developer.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            peerValid.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            wrongAudience.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            expired.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            await using Stream content = await developer.Content.ReadAsStreamAsync(cancellation.Token);
            ApplicationExportDocument export = await ApplicationExportDocument.LoadAsync(content, cancellation.Token);
            export.Application.ShouldBe("appa");
            export.Model.Resources.Count.ShouldBe(1);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            File.Exists(Path.Combine(root, "appa", "control-plane.json")).ShouldBeFalse();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        HttpClient client,
        Uri endpoint,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
