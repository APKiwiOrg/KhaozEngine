using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// One route query spread over several owner calls. <see cref="IIncrementalRegionPathPlanner.BeginPath"/> freezes
/// its inputs: the start feet, the goal region, the agent radius and the <see cref="PathQueryBudget"/>. Each
/// <see cref="Advance"/> call spends work according to its <see cref="PathWorkBudget"/>. The request becomes
/// <see cref="PathPlanState.Ready"/> once the whole route is planned.
/// </summary>
/// <remarks>
/// <para>Route equality. For every quota schedule a Ready request holds the same status, waypoint count, kinds,
/// layers and position bits as the synchronous <see cref="IRegionPathPlanner.FindPath(Vector3, NavGoalRegion,
/// float, PathQueryBudget)"/> of the same planner on the same frozen inputs, including the one-shot fallback
/// state read at begin by a planner that straightens routes. A spent quota never yields a provisional route.</para>
/// <para>Region predicate. The goal region's predicate must stay pure for the request's whole life, from
/// <see cref="IIncrementalRegionPathPlanner.BeginPath"/> until the request is Ready or Cancelled, not only during
/// one call. A predicate that reads state its owner changes later, such as the mover's current target, plans
/// against a goal that no longer exists. A predicate must not call back into the planner or its pool.</para>
/// <para>Publication. The call on which a request becomes Ready depends on its ordered operations and weights,
/// the quotas on the owner's advances, and the advance on which it first holds a workspace (see
/// <see cref="PathPlanWorkspaces"/>). It does not depend on clocks, task or thread scheduling, allocations,
/// collections, JIT tier, other pools or synchronous queries. With one workspace per sharing owner, no request
/// waits. The owner's adoption rule determines when it uses the Ready result.</para>
/// <para>Lifetime. A request is not serializable. Cancel or dispose a request its owner no longer needs, before
/// dropping it, so its workspace passes to the next waiter. A dropped pending request keeps its lease. Cancel and
/// Dispose are idempotent, and neither changes a Ready request or its result.</para>
/// </remarks>
internal abstract class PathPlanRequest : IDisposable
{
    NavPath? _result;

    /// <summary>Creates a pending request with no work spent.</summary>
    private protected PathPlanRequest()
    {
    }

    /// <summary>Pending until the route is planned, then Ready, or Cancelled once cancelled or disposed while
    /// pending.</summary>
    public PathPlanState State { get; private set; }

    /// <summary>Null until the request is <see cref="PathPlanState.Ready"/>, then the immutable planned route,
    /// which may be <see cref="NavPath.Unreachable"/>. Null after a cancel.</summary>
    public NavPath? Result => State == PathPlanState.Ready ? _result : null;

    /// <summary>The work units this request has spent so far. For a completed request, the final value depends
    /// only on the frozen inputs and the operation weights. Cancellation preserves the work already spent.</summary>
    public long WorkSpent { get; private set; }

    /// <summary>
    /// Spends at most the larger of <paramref name="budget"/> and the largest operation weight on a pending
    /// request, performing whole operations in order. A request that has no workspace yet waits and spends
    /// nothing. A Ready or Cancelled request spends nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget"/> holds fewer than one unit, as the
    /// default value does.</exception>
    /// <exception cref="InvalidOperationException">The call overlaps another call into the pool.</exception>
    public void Advance(PathWorkBudget budget)
    {
        if (budget.Units < 1)
            throw new ArgumentOutOfRangeException(nameof(budget), "A work budget holds at least one unit.");
        EnterCall();
        try
        {
            if (State == PathPlanState.Pending) AdvanceCore(budget);
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>
    /// Whether a route planned from the frozen start also serves <paramref name="feet"/>: the same bits, or other
    /// feet that resolve to the same start node (the layer and cell the planner starts from) with the profile's
    /// start resolution passing. An owner checks it before adopting the result of a request that finished after
    /// its body could have moved.
    /// </summary>
    public abstract bool StartsFrom(Vector3 feet);

    /// <summary>Cancels a pending request and releases its workspace, or leaves the pool's queue if it was still
    /// waiting. Does nothing to a Ready or Cancelled request.</summary>
    /// <exception cref="InvalidOperationException">The call overlaps another call into the pool.</exception>
    public void Cancel()
    {
        EnterCall();
        try
        {
            CancelPending();
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Cancels while the caller already owns the pool's call guard. Also used on query failure.</summary>
    private protected void CancelPending()
    {
        if (State != PathPlanState.Pending) return;
        State = PathPlanState.Cancelled;
        Release();
    }

    /// <summary>Cancels the request if it is still pending. Idempotent.</summary>
    public void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }

    /// <summary>Acquires an owner sequence guard before an advance or cancel can change request state.</summary>
    private protected virtual void EnterCall() { }

    /// <summary>Releases a successfully acquired owner sequence guard.</summary>
    private protected virtual void ExitCall() { }

    /// <summary>Spends work according to <paramref name="budget"/>. Called only while the request is pending,
    /// with a quota of at least one unit. An implementation reports each operation through
    /// <see cref="Spend"/> and finishes through <see cref="Complete"/>.</summary>
    private protected abstract void AdvanceCore(PathWorkBudget budget);

    /// <summary>Returns the request's workspace to its pool, or leaves the pool's queue. Called exactly once,
    /// when the request leaves <see cref="PathPlanState.Pending"/> by completion or cancellation.</summary>
    private protected abstract void Release();

    /// <summary>Adds the <paramref name="units"/> an operation just performed to <see cref="WorkSpent"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is below one.</exception>
    /// <exception cref="InvalidOperationException">The request is not pending.</exception>
    private protected void Spend(int units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        if (State != PathPlanState.Pending)
            throw new InvalidOperationException("Only a pending request can spend work.");
        WorkSpent += units;
    }

    /// <summary>Publishes <paramref name="result"/>, makes the request Ready and releases its workspace.</summary>
    /// <exception cref="InvalidOperationException">The request is not pending.</exception>
    private protected void Complete(NavPath result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (State != PathPlanState.Pending)
            throw new InvalidOperationException("Only a pending request can complete.");
        _result = result;
        State = PathPlanState.Ready;
        Release();
    }
}
