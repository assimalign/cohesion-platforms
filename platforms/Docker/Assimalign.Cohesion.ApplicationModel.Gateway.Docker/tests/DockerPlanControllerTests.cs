using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerPlanControllerTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Controller: Should create network, volume, container, start, and input archive through Engine API")]
    public async Task ReconcileAsync_OnPlanWithRuntimeInputs_ShouldSendCompleteEngineRequestsInOrder()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        var options = CreateOptions();
        DockerPlanController controller = CreateController(options, engine, out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create(
            probes: [new ProbeMapping("liveness", "http", ProbeKind.Tcp, null, [])],
            includeVolume: true,
            includeInputs: true,
            expose: true);
        observer.Start();

        try
        {
            // Act
            await controller.ReconcileAsync(context, CancellationToken.None);

            // Assert
            DockerEngineRequest[] mutations = engineServer.RecordedRequests
                .Where(request => request.Method is "POST" or "PUT")
                .Where(request => request.Path != "/exec")
                .ToArray();
            mutations.Select(request => $"{request.Method} {request.RawUrl}").ShouldBe(
            [
                "POST /v1.51/networks/create",
                "POST /v1.51/volumes/create",
                "POST /v1.51/containers/create?name=appa-worker",
                "PUT /v1.51/containers/container-1/archive?path=%2F&noOverwriteDirNonDir=false",
                "POST /v1.51/containers/container-1/start",
                "PUT /v1.51/containers/container-1/archive?path=%2F&noOverwriteDirNonDir=false",
            ]);

            DockerEngineRequest create = mutations.Single(
                request => request.Path == "/containers/create");
            DockerContainerCreateRequest createRequest = JsonSerializer.Deserialize(
                create.Body,
                DockerEngineJsonContext.Default.DockerContainerCreateRequest)!;
            createRequest.Image.ShouldBe($"sha256:{new string('b', 64)}");
            createRequest.Labels![DockerMetadata.ApplicationLabel].ShouldBe("appa");
            createRequest.Labels[DockerMetadata.OwnerLabel].ShouldBe("appa@docker");
            createRequest.Labels[DockerMetadata.ResourceLabel].ShouldBe("worker");
            createRequest.Labels[DockerMetadata.PlanHashLabel].ShouldNotBeNullOrWhiteSpace();
            createRequest.Labels[DockerMetadata.RuntimeHashLabel].ShouldNotBeNullOrWhiteSpace();
            string[] environment = createRequest.Env
                ?? throw new InvalidDataException("The Docker request omitted its environment.");
            environment.ShouldContain("COHESION_ENDPOINT_HTTP_PUBLIC_URL=http://localhost:8080");
            createRequest.HostConfig!.NetworkMode.ShouldBe("cohesion-appa");
            createRequest.HostConfig.Mounts!.ShouldHaveSingleItem().Source
                .ShouldBe("appa-worker-data");
            createRequest.HostConfig.Tmpfs!.Keys.OrderBy(static value => value).ShouldBe(
                ["/run/secrets", "/var/run/cohesion"]);
            createRequest.HostConfig.Tmpfs.Values.ShouldAllBe(
                value => value == "rw,noexec,nosuid,nodev,mode=0700");
            DockerPortBinding[] bindings = createRequest.HostConfig.PortBindings!["8080/tcp"]!;
            bindings.Length.ShouldBe(2);
            bindings[0].HostIp.ShouldBe("0.0.0.0");
            bindings[0].HostPort.ShouldBe("8080");
            bindings[1].HostIp.ShouldBe("127.0.0.1");
            bindings[1].HostPort.ShouldBeEmpty();
            string[] aliases = createRequest.NetworkingConfig!
                .EndpointsConfig!["cohesion-appa"].Aliases
                ?? throw new InvalidDataException("The Docker request omitted network aliases.");
            aliases.ShouldContain("worker");

            DockerEngineRequest[] archives = mutations
                .Where(request => request.Method == "PUT")
                .ToArray();
            archives.Length.ShouldBe(2);
            archives.ShouldAllBe(request => request.ContentType == "application/x-tar");
            IReadOnlyDictionary<string, ArchiveEntry> configuration = ReadArchive(archives[0].Body);
            IReadOnlyDictionary<string, ArchiveEntry> sensitive = ReadArchive(archives[1].Body);
            Encoding.UTF8.GetString(configuration["app/settings.json"].Content)
                .ShouldBe("{\"enabled\":true}");
            configuration.ShouldNotContainKey("run/secrets/api-key");
            configuration.ShouldNotContainKey("var/run/cohesion/bootstrap.token");
            Encoding.UTF8.GetString(sensitive["run/secrets/api-key"].Content)
                .ShouldBe("top-secret");
            Encoding.UTF8.GetString(sensitive["var/run/cohesion/bootstrap.token"].Content)
                .ShouldBe("bootstrap-token");
            sensitive["run/secrets/api-key"].Mode.ShouldBe(UnixFileMode.UserRead);
            sensitive["var/run/cohesion/bootstrap.token"].Mode.ShouldBe(UnixFileMode.UserRead);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Controller: Should leave an already-conforming running container unchanged")]
    public async Task ReconcileAsync_OnConformingRunningContainer_ShouldPerformNoMutation()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerPlanController controller = CreateController(
            CreateOptions(),
            engine,
            out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create(includeVolume: true);
        observer.Start();

        try
        {
            await controller.ReconcileAsync(context, CancellationToken.None);
            engineServer.ClearRecordedRequests();

            // Act
            await controller.ReconcileAsync(context, CancellationToken.None);

            // Assert
            engineServer.RecordedRequests.ShouldNotContain(request =>
                request.Method == "POST"
                || request.Method == "PUT"
                || request.Method == "DELETE");
            engineServer.ContainerCount.ShouldBe(1);
            engineServer.VolumeCount.ShouldBe(1);
            engineServer.NetworkCount.ShouldBe(1);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Controller: Should replace a container when runtime inputs diverge")]
    public async Task ReconcileAsync_OnDivergentRuntimeHash_ShouldStopRemoveCreateStartAndRestage()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerPlanController controller = CreateController(
            CreateOptions(),
            engine,
            out DockerContainerObserver observer);
        DockerTestContext original = DockerTestContext.Create(includeInputs: true);
        DockerTestContext changed = DockerTestContext.Create(
            includeInputs: true,
            settingsContent: "{\"enabled\":false}");
        observer.Start();

        try
        {
            await controller.ReconcileAsync(original, CancellationToken.None);
            DockerContainerCreateRequest originalRequest = JsonSerializer.Deserialize(
                engineServer.RecordedRequests.Single(request => request.Path == "/containers/create").Body,
                DockerEngineJsonContext.Default.DockerContainerCreateRequest)!;
            engineServer.ClearRecordedRequests();

            // Act
            await controller.ReconcileAsync(changed, CancellationToken.None);

            // Assert
            DockerEngineRequest[] mutations = engineServer.RecordedRequests
                .Where(request => request.Method is "POST" or "PUT" or "DELETE")
                .ToArray();
            mutations.Select(request => $"{request.Method} {request.RawUrl}").ShouldBe(
            [
                "POST /v1.51/containers/container-1/stop?t=30",
                "DELETE /v1.51/containers/container-1?force=false&v=false",
                "POST /v1.51/containers/create?name=appa-worker",
                "PUT /v1.51/containers/container-2/archive?path=%2F&noOverwriteDirNonDir=false",
                "POST /v1.51/containers/container-2/start",
                "PUT /v1.51/containers/container-2/archive?path=%2F&noOverwriteDirNonDir=false",
            ]);
            DockerContainerCreateRequest changedRequest = JsonSerializer.Deserialize(
                mutations.Single(request => request.Path == "/containers/create").Body,
                DockerEngineJsonContext.Default.DockerContainerCreateRequest)!;
            changedRequest.Labels![DockerMetadata.PlanHashLabel]
                .ShouldBe(originalRequest.Labels![DockerMetadata.PlanHashLabel]);
            changedRequest.Labels[DockerMetadata.RuntimeHashLabel]
                .ShouldNotBe(originalRequest.Labels[DockerMetadata.RuntimeHashLabel]);
            engineServer.ContainerCount.ShouldBe(1);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Stop: Should use t=30 and retain container, network, and named volume")]
    public async Task StopAsync_OnRunningContainer_ShouldStopGracefullyWithoutDeletingPersistentObjects()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerPlanController controller = CreateController(
            CreateOptions(),
            engine,
            out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create(includeVolume: true);
        observer.Start();

        try
        {
            await controller.ReconcileAsync(context, CancellationToken.None);
            engineServer.ClearRecordedRequests();

            // Act
            await controller.StopAsync(context, CancellationToken.None);

            // Assert
            DockerEngineRequest stop = engineServer.RecordedRequests.Single(
                request => request.Method == "POST" && request.Path.EndsWith("/stop", StringComparison.Ordinal));
            stop.RawUrl.ShouldBe("/v1.51/containers/container-1/stop?t=30");
            engineServer.RecordedRequests.ShouldNotContain(request => request.Method == "DELETE");
            engineServer.ContainerCount.ShouldBe(1);
            engineServer.VolumeCount.ShouldBe(1);
            engineServer.NetworkCount.ShouldBe(1);
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Stopped);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Teardown: Should remove container, volume, and network in reverse order and remain idempotent")]
    public async Task DeleteAsync_OnOwnedObjectsAndRetry_ShouldDeleteInReverseOrderOnlyOnce()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerPlanController controller = CreateController(
            CreateOptions(),
            engine,
            out DockerContainerObserver observer);
        DockerTestContext context = DockerTestContext.Create(includeVolume: true);
        observer.Start();

        try
        {
            await controller.ReconcileAsync(context, CancellationToken.None);
            engineServer.ClearRecordedRequests();

            // Act
            await controller.DeleteAsync(context, CancellationToken.None);

            // Assert
            engineServer.RecordedRequests
                .Where(request => request.Method == "DELETE")
                .Select(request => request.Path)
                .ShouldBe(
                [
                    "/containers/container-1",
                    "/volumes/appa-worker-data",
                    "/networks/network-1",
                ]);
            engineServer.ContainerCount.ShouldBe(0);
            engineServer.VolumeCount.ShouldBe(0);
            engineServer.NetworkCount.ShouldBe(0);

            engineServer.ClearRecordedRequests();
            await controller.DeleteAsync(context, CancellationToken.None);
            engineServer.RecordedRequests.ShouldNotContain(request => request.Method == "DELETE");
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Stopped);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    private static DockerGatewayOptions CreateOptions() => new()
    {
        ObservationInterval = TimeSpan.FromSeconds(30),
        ProbeInterval = TimeSpan.FromSeconds(30),
        EventReconnectDelay = TimeSpan.FromSeconds(30),
    };

    private static DockerPlanController CreateController(
        DockerGatewayOptions options,
        IDockerEngineClient engine,
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
            observer);
        return controller;
    }

    private static IReadOnlyDictionary<string, ArchiveEntry> ReadArchive(byte[] content)
    {
        var result = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new TarReader(stream, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType is not TarEntryType.RegularFile || entry.DataStream is null)
            {
                continue;
            }

            using var destination = new MemoryStream();
            entry.DataStream.CopyTo(destination);
            result.Add(entry.Name, new ArchiveEntry(destination.ToArray(), entry.Mode));
        }

        return result;
    }

    private sealed record ArchiveEntry(byte[] Content, UnixFileMode Mode);
}
