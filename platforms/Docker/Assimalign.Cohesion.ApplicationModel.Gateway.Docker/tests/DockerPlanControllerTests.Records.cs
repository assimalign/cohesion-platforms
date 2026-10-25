using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public partial class DockerPlanControllerTests
{
    private static readonly IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> _noClaims =
        new Dictionary<ApplicationName, ReadOnlyMemory<byte>>();

    [Fact(DisplayName = "Cohesion Test [Docker] - Records: a new container receives its record before it starts")]
    public async Task ReconcileAsync_WithRecord_ShouldStageTheRecordBeforeStart()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        ResourceRecord record = ResourceRecord.Create(
            "g1", Json("""{"Web:Cors":{"origin":"a"}}"""), _noClaims, Json("""{"Web:Cors":{"origin":"b"}}"""));
        DockerPlanController controller = CreateRecordController(CreateOptions(), engine, record, out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create().WithDeploymentRecord("g1");
        observer.Start();

        try
        {
            // Act
            await controller.ReconcileAsync(context, CancellationToken.None);

            // Assert
            string[] mutations = engineServer.RecordedRequests
                .Where(request => request.Method is "POST" or "PUT" && request.Path != "/exec")
                .Select(request => request.Method + " " + request.Path)
                .ToArray();
            int start = Array.IndexOf(mutations, "POST /containers/container-1/start");
            int staged = Array.FindIndex(mutations, request => request == "PUT /containers/container-1/archive");
            staged.ShouldBeGreaterThanOrEqualTo(0);
            staged.ShouldBeLessThan(start);
            IReadOnlyDictionary<string, ArchiveEntry> files = ReadRecordArchive(engineServer);
            Encoding.UTF8.GetString(files["var/run/cohesion-config/deployment.json"].Content).ShouldContain("\"a\"", Case.Sensitive);
            Encoding.UTF8.GetString(files["var/run/cohesion-config/override.json"].Content).ShouldContain("\"b\"", Case.Sensitive);
            JsonEnvironment(engineServer).ShouldContain(AppEnvironment.Variables.ConfigDirectory + "=/var/run/cohesion-config");
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Records: a management change is copied into the running container, and a withdrawn layer becomes an empty object")]
    public async Task DeliverRecordAsync_OnRunningContainer_ShouldCopyAndWithdrawLayers()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        ResourceRecord first = ResourceRecord.Create(
            "g1",
            default,
            new Dictionary<ApplicationName, ReadOnlyMemory<byte>> { ["appb"] = Json("""{"Orders:Schema":{"invoices":{}}}""") },
            Json("""{"Web:Cors":{"origin":"b"}}"""));
        DockerPlanController controller = CreateRecordController(CreateOptions(), engine, first, out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create().WithDeploymentRecord("g1");
        observer.Start();

        try
        {
            await controller.ReconcileAsync(context, CancellationToken.None);
            engineServer.ClearRecordedRequests();
            ResourceRecord second = ResourceRecord.Create("g1", default, _noClaims, default);

            // Act
            await controller.DeliverRecordAsync("appa", "worker", first, second, CancellationToken.None);

            // Assert
            IReadOnlyDictionary<string, ArchiveEntry> files = ReadRecordArchive(engineServer);
            Encoding.UTF8.GetString(files["var/run/cohesion-config/override.json"].Content).ShouldBe("{}");
            Encoding.UTF8.GetString(files["var/run/cohesion-config/claims.appb.json"].Content).ShouldBe("{}");
            engineServer.RecordedRequests.ShouldNotContain(request => request.Path.EndsWith("/start", StringComparison.Ordinal));
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Records: a stopped or missing container receives nothing until it starts")]
    public async Task DeliverRecordAsync_WithoutRunningContainer_ShouldCopyNothing()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        ResourceRecord record = ResourceRecord.Create("g1", default, _noClaims, Json("""{"A":{}}"""));
        DockerPlanController controller = CreateRecordController(CreateOptions(), engine, record, out _);

        // Act
        await controller.DeliverRecordAsync("appa", "worker", record, record, CancellationToken.None);

        // Assert
        engineServer.RecordedRequests.ShouldNotContain(request => request.Method == "PUT");
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Records: a fresh container holds only the record's layers")]
    public void CreateFiles_WithoutPreviousRecord_ShouldWriteOnlyPresentLayers()
    {
        // Arrange
        ResourceRecord record = ResourceRecord.Create("g1", Json("""{"A":{}}"""), _noClaims, default);

        // Act
        IReadOnlyList<DockerFilePlan> files = DockerResourceRecordStore.CreateFiles(record, previous: null);

        // Assert
        files.Select(static file => file.Path).ShouldBe(["/var/run/cohesion-config/deployment.json"]);
        files.ShouldAllBe(static file => !file.Sensitive);
    }

    private static DockerPlanController CreateRecordController(
        DockerGatewayOptions options,
        IDockerEngineClient engine,
        ResourceRecord record,
        out DockerContainerObserver observer)
    {
        DockerPlanController? controller = null;
        observer = new DockerContainerObserver(
            options,
            () => engine,
            (context, compilation, cancellationToken) =>
                (controller ?? throw new InvalidOperationException("Controller is not initialized."))
                .RestartAsync(context, compilation, cancellationToken));
        controller = new DockerPlanController(
            options,
            new DockerPlanCompiler(),
            () => engine,
            observer,
            (_, _) => ValueTask.FromResult<ResourceRecord?>(record));
        return controller;
    }

    // The archive that carries the record: the one whose entries are under the record directory.
    private static IReadOnlyDictionary<string, ArchiveEntry> ReadRecordArchive(FakeDockerEngine engineServer)
    {
        foreach (DockerEngineRequest request in engineServer.RecordedRequests.Where(static request => request.Method == "PUT"))
        {
            IReadOnlyDictionary<string, ArchiveEntry> entries = ReadArchive(request.Body);
            if (entries.Keys.Any(static name => name.StartsWith("var/run/cohesion-config/", StringComparison.Ordinal)))
            {
                return entries;
            }
        }

        throw new InvalidOperationException("No archive carried the deployment record.");
    }

    private static string[] JsonEnvironment(FakeDockerEngine engineServer)
    {
        DockerEngineRequest create = engineServer.RecordedRequests.Single(static request => request.Path == "/containers/create");
        DockerContainerCreateRequest body = System.Text.Json.JsonSerializer.Deserialize(
            create.Body, DockerEngineJsonContext.Default.DockerContainerCreateRequest)!;
        return body.Env ?? [];
    }

    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);
}
