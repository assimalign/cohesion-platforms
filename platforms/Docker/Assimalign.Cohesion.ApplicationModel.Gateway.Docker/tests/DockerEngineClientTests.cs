using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerEngineClientTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Engine negotiation: Ping and version discovery should remain unversioned")]
    public async Task PingAsync_OnCompatibleEngine_ShouldUseUnversionedDiscoveryEndpoints()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);

        // Act
        await engine.PingAsync(CancellationToken.None);

        // Assert
        engineServer.RecordedRequests.Select(request => request.RawUrl).ShouldBe(
            ["/_ping", "/version"]);
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Engine negotiation: Should use the highest mutually supported API version")]
    [InlineData("1.60", "1.51")]
    [InlineData("1.41", "1.41")]
    public async Task InspectNetworkAsync_OnCompatibleApiRange_ShouldUseNegotiatedVersion(
        string serverMaximum,
        string expectedVersion)
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine
        {
            ApiVersion = serverMaximum,
            MinimumApiVersion = "1.24",
        };
        using var engine = new DockerEngineClient(engineServer.Endpoint);

        // Act
        DockerVersionResponse first = await engine.GetVersionAsync(CancellationToken.None);
        DockerNetworkInspectResponse? network = await engine.InspectNetworkAsync(
            "missing-network",
            CancellationToken.None);
        DockerVersionResponse second = await engine.GetVersionAsync(CancellationToken.None);

        // Assert
        first.ShouldBeSameAs(second);
        first.ApiVersion.ShouldBe(serverMaximum);
        network.ShouldBeNull();
        engineServer.RecordedRequests.Select(request => request.RawUrl).ShouldBe(
        [
            "/version",
            $"/v{expectedVersion}/networks/missing-network",
        ]);
        engineServer.RecordedRequests.Select(request => request.Path).ShouldBe(
            ["/version", "/networks/missing-network"]);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Engine negotiation: Concurrent first use should negotiate once")]
    public async Task InspectNetworkAsync_OnConcurrentFirstUse_ShouldNegotiateOnce()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        Task<DockerNetworkInspectResponse?>[] operations = Enumerable.Range(0, 8)
            .Select(index => engine.InspectNetworkAsync(
                $"missing-{index}",
                CancellationToken.None))
            .ToArray();

        // Act
        await Task.WhenAll(operations);

        // Assert
        engineServer.RecordedRequests.Count(request => request.RawUrl == "/version").ShouldBe(1);
        engineServer.RecordedRequests
            .Where(request => request.RawUrl != "/version")
            .ShouldAllBe(request => request.RawUrl.StartsWith("/v1.51/", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Engine errors: Should retain status, method, URL, and daemon detail")]
    public async Task CreateNetworkAsync_OnDaemonError_ShouldSurfaceStructuredRequestFailure()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine
        {
            NetworkCreateFailure = HttpStatusCode.Conflict,
        };
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        var request = new DockerNetworkCreateRequest
        {
            Name = "collision",
            Labels = new Dictionary<string, string>(),
        };

        // Act
        DockerEngineException exception = await Should.ThrowAsync<DockerEngineException>(
            () => engine.CreateNetworkAsync(request, CancellationToken.None));

        // Assert
        exception.Method.ShouldBe(HttpMethod.Post);
        exception.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        exception.RequestUri!.PathAndQuery.ShouldBe("/v1.51/networks/create");
        exception.ResponseBody.ShouldNotBeNull();
        exception.ResponseBody!.ShouldContain("forced network creation failure", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Engine negotiation: Should reject a server API range newer than the client")]
    public async Task GetVersionAsync_OnNonOverlappingApiRange_ShouldThrowNotSupportedException()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine
        {
            ApiVersion = "1.60",
            MinimumApiVersion = "1.52",
        };
        using var engine = new DockerEngineClient(engineServer.Endpoint);

        // Act
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
            () => engine.GetVersionAsync(CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("does not overlap supported client range", Case.Sensitive);
        exception.Message.ShouldContain("'1.52' through '1.60'", Case.Sensitive);
        engineServer.RecordedRequests.ShouldHaveSingleItem().RawUrl.ShouldBe("/version");
    }
}
