using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerContainerObserverTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Observer: Should combine event and inspect into Running, Failed, and observed endpoints")]
    public async Task ObserveAsync_OnContainerEventAndInspection_ShouldPublishLifecycleAndEndpoints()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerTestContext context = DockerTestContext.Create(expose: true, restartPolicy: RestartPolicy.Never);
        DockerPlanCompilation compilation = Compile(context);
        string containerId = engineServer.SeedContainer(
            compilation.Container.Name,
            CreateContainerRequest(compilation, "49152"));
        var observer = new DockerContainerObserver(
            CreateOptions(),
            () => engine,
            (_, _, _) => throw new InvalidOperationException("Restart was not expected."));
        observer.Start();

        try
        {
            // Act
            await observer.RegisterAsync(
                context,
                compilation,
                containerId,
                RestartPolicy.Never,
                CancellationToken.None);

            // Assert
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Running);
            IReadOnlyList<ResourceEndpoint> runningEndpoints =
                context.State.GetObservedEndpoints(context.Resource.Id);
            runningEndpoints.Count.ShouldBe(2);
            runningEndpoints.ShouldContain(endpoint =>
                endpoint.Name == "http"
                && endpoint.Scheme == "http"
                && endpoint.Host == "worker-http"
                && endpoint.Port == 8080
                && !endpoint.IsPublic);
            runningEndpoints.ShouldContain(endpoint =>
                endpoint.Name == "http"
                && endpoint.Scheme == "http"
                && endpoint.Host == "gateway.example.test"
                && endpoint.Port == 49152
                && endpoint.IsPublic);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<ResourceLifecycle> failed = context.State.WaitForStateAsync(
                context.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Failed },
                TimeSpan.FromSeconds(5),
                timeout.Token);
            engineServer.SetContainerState(containerId, running: false, exitCode: 64);
            await engineServer.PublishContainerEventAsync(
                containerId,
                "die",
                timeout.Token);

            (await failed).ShouldBe(ResourceLifecycle.Failed);
            context.State.GetObservedEndpoints(context.Resource.Id).ShouldBe(runningEndpoints);
            engineServer.RecordedRequests.ShouldContain(request =>
                request.Method == "GET" && request.Path == "/events");
            engineServer.RecordedRequests.Count(request =>
                request.Method == "GET" && request.Path.EndsWith("/json", StringComparison.Ordinal))
                .ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Liveness: Should publish Degraded immediately and restart after three failures for Always policy")]
    public async Task ObserveAsync_OnThreeLivenessFailuresWithAlwaysPolicy_ShouldDegradeThenRestart()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerTestContext context = DockerTestContext.Create(
            probes: [new ProbeMapping("liveness", null, ProbeKind.Exec, null, ["/bin/check"])],
            restartPolicy: RestartPolicy.Always);
        DockerPlanCompilation compilation = Compile(context);
        DockerContainerCreateRequest request = CreateContainerRequest(compilation);
        string containerId = engineServer.SeedContainer(compilation.Container.Name, request);
        engineServer.QueueExecExitCodes(1, 1, 1);
        var lifecycle = new List<ResourceLifecycle>();
        var lifecycleGate = new object();
        context.State.StateChanged += (_, args) =>
        {
            lock (lifecycleGate)
            {
                lifecycle.Add(args.Current);
            }
        };
        var restarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartCalls = 0;
        var observer = new DockerContainerObserver(
            CreateOptions(),
            () => engine,
            (_, _, _) =>
            {
                Interlocked.Increment(ref restartCalls);
                string replacement = engineServer.SeedContainer(compilation.Container.Name, request);
                restarted.TrySetResult(replacement);
                return Task.FromResult(replacement);
            });
        observer.Start();

        try
        {
            await observer.RegisterAsync(
                context,
                compilation,
                containerId,
                RestartPolicy.Always,
                CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Act & Assert: registration signals the first post-Running liveness pass.
            await WaitForAsync(
                () => CountExecCreates(engineServer) >= 1,
                timeout.Token);
            await WaitForAsync(
                () => context.State.GetState(context.Resource.Id) == ResourceLifecycle.Degraded,
                timeout.Token);
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Degraded);
            Volatile.Read(ref restartCalls).ShouldBe(0);

            await engineServer.PublishContainerEventAsync(containerId, "health_status", timeout.Token);
            await WaitForAsync(
                () => CountExecCreates(engineServer) >= 2,
                timeout.Token);
            await WaitForAsync(
                () => context.State.GetState(context.Resource.Id) == ResourceLifecycle.Degraded,
                timeout.Token);
            context.State.GetState(context.Resource.Id).ShouldBe(ResourceLifecycle.Degraded);
            Volatile.Read(ref restartCalls).ShouldBe(0);

            await engineServer.PublishContainerEventAsync(containerId, "health_status", timeout.Token);
            _ = await restarted.Task.WaitAsync(timeout.Token);

            Volatile.Read(ref restartCalls).ShouldBe(1);
            CountExecCreates(engineServer).ShouldBe(3);
            lock (lifecycleGate)
            {
                lifecycle.ShouldContain(ResourceLifecycle.Running);
                lifecycle.ShouldContain(ResourceLifecycle.Degraded);
                lifecycle.IndexOf(ResourceLifecycle.Degraded)
                    .ShouldBeLessThan(lifecycle.IndexOf(ResourceLifecycle.Stopping));
            }
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Stop: Should interrupt a supervised restart backoff")]
    public async Task BeginStop_OnPendingRestartBackoff_ShouldReleaseMutationPromptly()
    {
        // Arrange
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        DockerTestContext context = DockerTestContext.Create(
            probes: [new ProbeMapping("liveness", null, ProbeKind.Exec, null, ["/bin/check"])],
            restartPolicy: RestartPolicy.Always);
        DockerPlanCompilation compilation = Compile(context);
        string containerId = engineServer.SeedContainer(
            compilation.Container.Name,
            CreateContainerRequest(compilation));
        engineServer.QueueExecExitCodes(1, 1, 1);
        DockerGatewayOptions options = CreateOptions();
        options.InitialRestartBackoff = TimeSpan.FromMinutes(1);
        options.MaximumRestartBackoff = TimeSpan.FromMinutes(1);
        var observer = new DockerContainerObserver(
            options,
            () => engine,
            (_, _, _) => throw new InvalidOperationException("A stopped resource must not restart."));
        observer.Start();

        try
        {
            await observer.RegisterAsync(
                context,
                compilation,
                containerId,
                RestartPolicy.Always,
                CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForAsync(() => CountExecCreates(engineServer) >= 1, timeout.Token);
            await engineServer.PublishContainerEventAsync(containerId, "health_status", timeout.Token);
            await WaitForAsync(() => CountExecCreates(engineServer) >= 2, timeout.Token);
            await engineServer.PublishContainerEventAsync(containerId, "health_status", timeout.Token);
            await WaitForAsync(
                () => context.State.GetState(context.Resource.Id) == ResourceLifecycle.Stopping,
                timeout.Token);

            // Act
            var elapsed = Stopwatch.StartNew();
            observer.BeginStop(context);
            using (await observer.EnterMutationAsync(context, timeout.Token))
            {
            }

            elapsed.Stop();

            // Assert
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            CountExecCreates(engineServer).ShouldBe(3);
        }
        finally
        {
            await observer.StopAsync();
        }
    }

    private static DockerGatewayOptions CreateOptions() => new()
    {
        PublicHost = "gateway.example.test",
        ObservationInterval = TimeSpan.FromSeconds(30),
        ProbeInterval = TimeSpan.FromSeconds(30),
        ProbeTimeout = TimeSpan.FromSeconds(2),
        LivenessFailureThreshold = 3,
        InitialRestartBackoff = TimeSpan.Zero,
        MaximumRestartBackoff = TimeSpan.Zero,
        EventReconnectDelay = TimeSpan.FromSeconds(30),
    };

    private static DockerPlanCompilation Compile(DockerTestContext context) =>
        new DockerPlanCompiler().Compile(
            context.Plan,
            context.GetArtifact<IContainerImageArtifact>(),
            context.Inputs,
            context.ObservedDependencies,
            context.Model.Name,
            context.Model.Owner);

    private static DockerContainerCreateRequest CreateContainerRequest(
        DockerPlanCompilation compilation,
        string publicHostPort = "")
    {
        Dictionary<string, DockerPortBinding[]?>? portBindings =
            compilation.Container.PortBindings.Count == 0
                ? null
                : new Dictionary<string, DockerPortBinding[]?>(StringComparer.Ordinal)
                {
                    ["8080/tcp"] =
                    [
                        new DockerPortBinding
                        {
                            HostIp = "0.0.0.0",
                            HostPort = publicHostPort,
                        },
                    ],
                };
        return new DockerContainerCreateRequest
        {
            Image = compilation.Container.Image,
            Labels = new Dictionary<string, string>(compilation.Container.Labels),
            HostConfig = new DockerHostConfig
            {
                NetworkMode = compilation.Network.Name,
                PortBindings = portBindings,
            },
            NetworkingConfig = new DockerNetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, DockerEndpointSettings>
                {
                    [compilation.Network.Name] = new DockerEndpointSettings
                    {
                        Aliases = [.. compilation.Container.NetworkAliases],
                    },
                },
            },
        };
    }

    private static int CountExecCreates(FakeDockerEngine engine) =>
        engine.RecordedRequests.Count(request =>
            request.Method == "POST"
            && request.Path.EndsWith("/exec", StringComparison.Ordinal));

    private static async Task WaitForAsync(
        Func<bool> condition,
        CancellationToken cancellationToken = default)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }
}
