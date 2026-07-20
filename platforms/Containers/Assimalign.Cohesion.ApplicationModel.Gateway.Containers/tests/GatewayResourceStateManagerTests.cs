using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class GatewayResourceStateManagerTests
{
    private static readonly IReadOnlySet<ResourceLifecycle> _runningOrFailed =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed };

    private static readonly IReadOnlySet<ResourceLifecycle> _runningOnly =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running };

    private static ResourceId NewId() => Guid.NewGuid();

    [Fact(DisplayName = "Cohesion Test [Containers] - GetState: Should return Unknown for a never-observed resource")]
    public void GetState_UnseenResource_ReturnsUnknown()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();

        // Act
        ResourceLifecycle state = manager.GetState(NewId());

        // Assert
        state.ShouldBe(ResourceLifecycle.Unknown);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should complete when the terminal set is hit with Running")]
    public async Task WaitForStateAsync_SetToRunningWhileWaiting_CompletesWithRunning()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, _runningOrFailed, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Act
        manager.SetState(id, ResourceLifecycle.Running);

        // Assert
        (await wait).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should complete when the terminal set is hit with Failed")]
    public async Task WaitForStateAsync_SetToFailedWhileWaiting_CompletesWithFailed()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, _runningOrFailed, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Act
        manager.SetState(id, ResourceLifecycle.Failed);

        // Assert
        (await wait).ShouldBe(ResourceLifecycle.Failed);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should return immediately when the state is already terminal")]
    public async Task WaitForStateAsync_StateAlreadyTerminal_ReturnsImmediately()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Running);

        // Act
        ResourceLifecycle reached = await manager.WaitForStateAsync(id, _runningOrFailed, TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        reached.ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should return the last observed state when the budget elapses")]
    public async Task WaitForStateAsync_BudgetElapses_ReturnsLastObservedState()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Starting);

        // Act
        ResourceLifecycle reached = await manager.WaitForStateAsync(id, _runningOnly, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        // Assert
        reached.ShouldBe(ResourceLifecycle.Starting);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should return the last observed state on cancellation instead of throwing")]
    public async Task WaitForStateAsync_Cancelled_ReturnsLastObservedState()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Provisioning);
        using var cancellation = new CancellationTokenSource();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, _runningOnly, Timeout.InfiniteTimeSpan, cancellation.Token);

        // Act
        cancellation.Cancel();

        // Assert
        (await wait).ShouldBe(ResourceLifecycle.Provisioning);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should complete on a later set when the budget is infinite")]
    public async Task WaitForStateAsync_InfiniteBudget_CompletesOnLaterSet()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, _runningOrFailed, Timeout.InfiniteTimeSpan, CancellationToken.None);
        wait.IsCompleted.ShouldBeFalse();

        // Act
        manager.SetState(id, ResourceLifecycle.Running);

        // Assert
        (await wait).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - WaitForStateAsync: Should not complete when an unrelated resource reaches a terminal state")]
    public async Task WaitForStateAsync_UnrelatedResourceSet_DoesNotCrossSignal()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId waited = NewId();
        ResourceId unrelated = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(waited, _runningOrFailed, Timeout.InfiniteTimeSpan, CancellationToken.None);

        // Act
        manager.SetState(unrelated, ResourceLifecycle.Running);

        // Assert
        wait.IsCompleted.ShouldBeFalse();
        manager.SetState(waited, ResourceLifecycle.Failed);
        (await wait).ShouldBe(ResourceLifecycle.Failed);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - SetState: Should expose observed endpoints through GetObservedEndpoints")]
    public void SetState_WithObservedEndpoints_ExposesThem()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();

        // Act
        manager.SetState(
            id,
            ResourceLifecycle.Running,
            observedEndpoints: new[] { new ResourceEndpoint("http", "http", 8080, Host: "localhost") });

        // Assert
        ResourceEndpoint endpoint = manager.GetObservedEndpoints(id).ShouldHaveSingleItem();
        endpoint.Port.ShouldBe(8080);
        endpoint.Host.ShouldBe("localhost");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - SetState: Should replace the endpoint list on a subsequent publish")]
    public void SetState_WithNewEndpoints_ReplacesPreviousList()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(
            id,
            ResourceLifecycle.Starting,
            observedEndpoints: new[] { new ResourceEndpoint("http", "http", 8080, Host: "localhost") });

        // Act
        manager.SetState(
            id,
            ResourceLifecycle.Running,
            observedEndpoints: new[]
            {
                new ResourceEndpoint("http", "http", 9090, Host: "service.cluster.local"),
                new ResourceEndpoint("grpc", "tcp", 9091, Host: "service.cluster.local"),
            });

        IReadOnlyList<ResourceEndpoint> endpoints = manager.GetObservedEndpoints(id);

        // Assert
        endpoints.Count.ShouldBe(2);
        endpoints[0].Port.ShouldBe(9090);
        endpoints[1].Name.ShouldBe("grpc");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - SetState: Should retain the endpoint list when a later set omits endpoints")]
    public void SetState_WithoutEndpoints_RetainsPreviousList()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(
            id,
            ResourceLifecycle.Running,
            observedEndpoints: new[] { new ResourceEndpoint("http", "http", 8080, Host: "localhost") });

        // Act
        manager.SetState(id, ResourceLifecycle.Degraded);

        // Assert
        ResourceEndpoint endpoint = manager.GetObservedEndpoints(id).ShouldHaveSingleItem();
        endpoint.Port.ShouldBe(8080);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - GetObservedEndpoints: Should return an empty list for a never-observed resource")]
    public void GetObservedEndpoints_UnseenResource_ReturnsEmpty()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();

        // Act
        IReadOnlyList<ResourceEndpoint> endpoints = manager.GetObservedEndpoints(NewId());

        // Assert
        endpoints.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - SetState: Should raise StateChanged carrying the previous and current states")]
    public void SetState_OnTransition_RaisesStateChangedWithPreviousAndCurrent()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        ResourceStateChangedEventArgs? captured = null;
        manager.StateChanged += (_, args) => captured = args;

        // Act
        manager.SetState(id, ResourceLifecycle.Failed, detail: "container exited with code 1");

        // Assert
        captured.ShouldNotBeNull();
        captured!.Resource.ShouldBe(id);
        captured.Previous.ShouldBe(ResourceLifecycle.Unknown);
        captured.Current.ShouldBe(ResourceLifecycle.Failed);
        captured.Detail.ShouldBe("container exited with code 1");
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - SetState: Should not raise StateChanged for a same-state idempotent write")]
    public void SetState_SameState_DoesNotRaiseStateChanged()
    {
        // Arrange
        var manager = new GatewayResourceStateManager();
        ResourceId id = NewId();
        int raised = 0;
        manager.StateChanged += (_, _) => raised++;
        manager.SetState(id, ResourceLifecycle.Running);

        // Act
        manager.SetState(id, ResourceLifecycle.Running);

        // Assert
        raised.ShouldBe(1);
    }
}
