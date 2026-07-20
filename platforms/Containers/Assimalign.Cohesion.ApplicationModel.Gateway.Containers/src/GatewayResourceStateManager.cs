using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// An in-memory, thread-safe <see cref="IApplicationResourceStateManager"/> for container
/// platform gateways. The platform's single observer writes observed lifecycle state and
/// allocated endpoints; controllers and the gateway's reconcile loop read them and gate
/// dependency readiness through <see cref="WaitForStateAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// All state reads, state writes, and waiter registration are serialized by a single lock,
/// which makes the readiness wait race-free: <see cref="WaitForStateAsync"/> inspects the
/// current state and registers its waiter without releasing the lock in between, so a
/// concurrent <see cref="SetState"/> can never slip through unobserved.
/// </para>
/// <para>
/// Side effects escape the lock: pending waiters are completed and <see cref="StateChanged"/>
/// is raised only after the lock is released, so handlers and continuations can safely call
/// back into this instance. Waiter continuations always run asynchronously.
/// </para>
/// </remarks>
public sealed class GatewayResourceStateManager : IApplicationResourceStateManager
{
    // Sentinel outside the enum's legitimate range, used only to mark a wait whose
    // budget or cancellation token elapsed before a terminal state was reached.
    private const ResourceLifecycle elapsed = (ResourceLifecycle)(-1);

    private readonly object _gate = new();
    private readonly Dictionary<ResourceId, Entry> _entries = new();

    /// <summary>
    /// Raised after a resource's observed state transitions to a different value. Not raised
    /// for idempotent writes that leave the state unchanged. Handlers run outside the
    /// manager's internal lock.
    /// </summary>
    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Gets the current observed lifecycle state of a resource.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <returns>
    /// The most recently recorded state, or <see cref="ResourceLifecycle.Unknown"/> when the
    /// resource has never been observed.
    /// </returns>
    public ResourceLifecycle GetState(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) ? entry.State : ResourceLifecycle.Unknown;
        }
    }

    /// <summary>
    /// Gets the most recently published observed (allocated) endpoints for a resource.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <returns>
    /// The latest endpoint list published through <see cref="SetState"/>, or an empty list
    /// when no endpoints have been published for the resource.
    /// </returns>
    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) && entry.Endpoints is not null
                ? entry.Endpoints
                : Array.Empty<ResourceEndpoint>();
        }
    }

    /// <summary>
    /// Records an observed state for a resource. This is an idempotent level write: repeating
    /// the current state is harmless and raises no event. When the state actually changes,
    /// any waiter whose terminal set contains the new state is completed and
    /// <see cref="StateChanged"/> is raised — both outside the internal lock.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="state">The newly observed lifecycle state.</param>
    /// <param name="detail">
    /// An optional human-readable detail for the transition (for example a failure reason),
    /// carried on the <see cref="StateChanged"/> event.
    /// </param>
    /// <param name="observedEndpoints">
    /// The resource's allocated endpoints, when known. A non-<see langword="null"/> list
    /// replaces any previously published list; <see langword="null"/> leaves the previously
    /// published endpoints untouched.
    /// </param>
    public void SetState(
        ResourceId id,
        ResourceLifecycle state,
        string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null)
    {
        ResourceLifecycle previous;
        List<Waiter>? satisfied = null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            previous = entry.State;
            entry.State = state;

            if (observedEndpoints is not null)
            {
                entry.Endpoints = observedEndpoints;
            }

            if (entry.Waiters is not null)
            {
                for (int i = entry.Waiters.Count - 1; i >= 0; i--)
                {
                    if (entry.Waiters[i].Terminals.Contains(state))
                    {
                        (satisfied ??= new List<Waiter>()).Add(entry.Waiters[i]);
                        entry.Waiters.RemoveAt(i);
                    }
                }
            }
        }

        // Satisfied waiters and the transition event both fire outside the lock so that
        // continuations and handlers may re-enter this instance without deadlocking.
        if (satisfied is not null)
        {
            foreach (Waiter waiter in satisfied)
            {
                waiter.Completion.TrySetResult(state);
            }
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, new ResourceStateChangedEventArgs(id, previous, state, detail));
        }
    }

    /// <summary>
    /// Waits until the resource's observed state becomes a member of
    /// <paramref name="terminals"/>, the <paramref name="budget"/> elapses, or
    /// <paramref name="cancellationToken"/> is signalled. Never throws for budget expiry or
    /// cancellation: both outcomes resolve to the last observed state so a caller gating on
    /// readiness always receives a level to act on.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="terminals">
    /// The membership set of states that completes the wait — for example
    /// <c>{ Running, Failed }</c> so a failed dependency cannot deadlock a dependent.
    /// </param>
    /// <param name="budget">
    /// The maximum time to wait. Pass <see cref="Timeout.InfiniteTimeSpan"/> to wait without
    /// a time limit.
    /// </param>
    /// <param name="cancellationToken">Signals that the wait should be abandoned.</param>
    /// <returns>
    /// The terminal state that was reached, or the last observed state when the budget
    /// elapsed or the token was signalled first.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="terminals"/> is <see langword="null"/>.
    /// </exception>
    public async Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminals);

        Waiter waiter;
        lock (_gate)
        {
            ResourceLifecycle current = _entries.TryGetValue(id, out Entry? existing)
                ? existing.State
                : ResourceLifecycle.Unknown;

            if (terminals.Contains(current))
            {
                return current;
            }

            if (existing is null)
            {
                existing = new Entry();
                _entries[id] = existing;
            }

            // Registered under the same lock that produced the state read above, so a
            // SetState racing this wait either completed before the read or will find
            // the waiter registered — a wakeup can never be lost.
            waiter = new Waiter(terminals);
            (existing.Waiters ??= new List<Waiter>()).Add(waiter);
        }

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget != Timeout.InfiniteTimeSpan)
        {
            expiry.CancelAfter(budget);
        }

        using (expiry.Token.Register(static state => ((Waiter)state!).Completion.TrySetResult(elapsed), waiter))
        {
            ResourceLifecycle reached = await waiter.Completion.Task.ConfigureAwait(false);

            if (reached != elapsed)
            {
                return reached;
            }

            // The budget or token elapsed first: withdraw the waiter and resolve to the
            // last observed state rather than throwing.
            lock (_gate)
            {
                if (_entries.TryGetValue(id, out Entry? entry))
                {
                    entry.Waiters?.Remove(waiter);
                    return entry.State;
                }

                return ResourceLifecycle.Unknown;
            }
        }
    }

    private sealed class Entry
    {
        public ResourceLifecycle State { get; set; } = ResourceLifecycle.Unknown;

        public IReadOnlyList<ResourceEndpoint>? Endpoints { get; set; }

        public List<Waiter>? Waiters { get; set; }
    }

    private sealed class Waiter
    {
        public Waiter(IReadOnlySet<ResourceLifecycle> terminals)
        {
            Terminals = terminals;
        }

        public IReadOnlySet<ResourceLifecycle> Terminals { get; }

        public TaskCompletionSource<ResourceLifecycle> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
