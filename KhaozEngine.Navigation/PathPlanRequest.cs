using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// One route query spread over several owner calls. <see cref="IIncrementalRegionPathPlanner.BeginPath"/> freezes
/// its inputs: the start feet, the goal region, the agent radius and the <see cref="PathQueryBudget"/>. Each
/// <see cref="Advance"/> call spends at most its <see cref="PathWorkBudget"/> and the request becomes
/// <see cref="PathPlanState.Ready"/> once the whole route is planned.
/// </summary>
/// <remarks>
/// <para>Route equality. For every quota schedule a Ready request holds the same status, waypoint count, kinds,
/// layers and position bits as the synchronous <see cref="IRegionPathPlanner.FindPath(Vector3, NavGoalRegion,
/// float, PathQueryBudget)"/> of the same planner on the same frozen inputs. A spent quota never yields a
/// provisional route.</para>
/// <para>Region predicate. The goal region's predicate must stay pure for the request's whole life, from
/// <see cref="IIncrementalRegionPathPlanner.BeginPath"/> until the request is Ready or Cancelled, not only during
/// one call. A predicate that reads state its owner changes later, such as the mover's current target, plans
/// against a goal that no longer exists. A predicate must not call back into the planner or its pool.</para>
/// <para>Publication. The call on which a request becomes Ready depends on four things and nothing else, never on
/// a clock, thread, task, allocation, collection, JIT tier, another pool or a synchronous query: its total work,
/// set by its frozen inputs and the operation weights, the quota on each owner call and which calls its owner
/// makes, the call on which it gets a workspace (see <see cref="PathPlanWorkspaces"/>), and the owner's adoption
/// rule. With one workspace per sharing owner no request waits, and publication depends only on the request's own
/// inputs, quotas and owner calls.</para>
/// <para>Lifetime. A request is not serializable. Cancel or dispose a request its owner no longer needs, before
/// dropping it, so its workspace passes to the next waiter. A dropped pending request keeps its lease. Cancel and
/// Dispose are idempotent, and neither changes a Ready request or its result.</para>
/// </remarks>
internal abstract class PathPlanRequest : IDisposable
{
    NavPath? _result;

    /// <summary>Creates a pending request with no work spent.</summary>
    protected PathPlanRequest()
    {
    }

    /// <summary>Pending until the route is planned, then Ready, or Cancelled once cancelled or disposed while
    /// pending.</summary>
    public PathPlanState State { get; private set; }

    /// <summary>Null until the request is <see cref="PathPlanState.Ready"/>, then the immutable planned route,
    /// which may be <see cref="NavPath.Unreachable"/>. Null after a cancel.</summary>
    public NavPath? Result => State == PathPlanState.Ready ? _result : null;

    /// <summary>The work units this request has spent so far. The final value depends only on the frozen inputs
    /// and the operation weights, never on the quota schedule.</summary>
    public long WorkSpent { get; private set; }

    /// <summary>
    /// Spends at most the larger of <paramref name="budget"/> and the largest operation weight on a pending
    /// request, performing whole operations in order. A request that has no workspace yet waits and spends
    /// nothing. A Ready or Cancelled request spends nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget"/> holds fewer than one unit, as the
    /// default value does.</exception>
    public void Advance(PathWorkBudget budget)
    {
        if (budget.Units < 1)
            throw new ArgumentOutOfRangeException(nameof(budget), "A work budget holds at least one unit.");
        if (State != PathPlanState.Pending) return;
        AdvanceCore(budget);
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
    public void Cancel()
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

    /// <summary>Spends up to <paramref name="budget"/> on a pending request. Called only while the request is
    /// pending, with a quota of at least one unit. An implementation reports each operation through
    /// <see cref="Spend"/> and finishes through <see cref="Complete"/>.</summary>
    protected abstract void AdvanceCore(PathWorkBudget budget);

    /// <summary>Returns the request's workspace to its pool, or leaves the pool's queue. Called exactly once,
    /// when the request leaves <see cref="PathPlanState.Pending"/> by completion or cancellation.</summary>
    protected abstract void Release();

    /// <summary>Adds the <paramref name="units"/> an operation just performed to <see cref="WorkSpent"/>.</summary>
    protected void Spend(int units) => WorkSpent += units;

    /// <summary>Publishes <paramref name="result"/>, makes the request Ready and releases its workspace.</summary>
    /// <exception cref="InvalidOperationException">The request is not pending.</exception>
    protected void Complete(NavPath result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (State != PathPlanState.Pending)
            throw new InvalidOperationException("Only a pending request can complete.");
        _result = result;
        State = PathPlanState.Ready;
        Release();
    }
}
