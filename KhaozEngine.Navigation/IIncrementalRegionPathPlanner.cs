using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Engine-owned region planning in slices under a <see cref="PathWorkBudget"/>. Keep this
/// capability internal so the engine controls the checks that bind a follower to its planner's
/// navigation space and authorize its workspace pool.
/// A request returns the same route as the synchronous <see cref="IRegionPathPlanner.FindPath(Vector3,
/// NavGoalRegion, float, PathQueryBudget)"/>.
/// </summary>
internal interface IIncrementalRegionPathPlanner : IRegionPathPlanner
{
    /// <summary>Whether this planner can run every engine-owned query operation under a request quota.</summary>
    bool SupportsRequests { get; }

    /// <summary>The exact navigation space used by this planner's requests when <see cref="SupportsRequests"/> is true.</summary>
    NavSpace Space { get; }

    /// <summary>Creates a pool of <paramref name="count"/> search workspaces for this planner's requests.
    /// Each workspace's buffers are preallocated for the grid and the expansion cap in <paramref name="largestBudget"/>.
    /// The planner's workspace buffers never grow during an advance. Publishing a nonempty route allocates its
    /// result. Caller predicates may allocate their own storage. The declared expansion cap and snap radius limit
    /// every begin. Either walk policy is supported. See <see cref="PathPlanWorkspaces"/> for the lease rules.</summary>
    /// <exception cref="NotSupportedException">The planner requires synchronous queries. A grid traversal
    /// constraint can scan a footprint inside one search operation. Region goals also require surface heights
    /// on every grid layer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below one or
    /// <paramref name="largestBudget"/> has a NaN snap radius.</exception>
    /// <exception cref="ArgumentException">The budget requires a frontier larger than an array.</exception>
    PathPlanWorkspaces CreateWorkspaces(int count, PathQueryBudget largestBudget);

    /// <summary>Checks eligibility, ownership and declared budget before a follower keeps this pool.</summary>
    /// <exception cref="ArgumentException">The planner requires synchronous queries, the pool belongs to a
    /// different planner, or the budget exceeds a declared limit.</exception>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    /// <exception cref="InvalidOperationException">The call overlaps another call into the pool.</exception>
    void ValidateWorkspaces(PathPlanWorkspaces workspaces, PathQueryBudget budget);

    /// <summary>
    /// Freezes <paramref name="start"/>, <paramref name="goal"/>, <paramref name="agentRadius"/> and
    /// <paramref name="budget"/> into a pending request and spends no work. The request takes a free workspace from
    /// <paramref name="workspaces"/> now when nothing waits, else it joins the pool's queue. The owner advances it
    /// in the same call. The goal's predicate must stay pure until the request is Ready or Cancelled.
    /// </summary>
    /// <exception cref="NotSupportedException">The planner requires synchronous queries. Check
    /// <see cref="SupportsRequests"/> before creating a workspace or beginning a request.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="goal"/> or <paramref name="workspaces"/> is
    /// null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workspaces"/> belongs to another planner, or
    /// the query budget exceeds either declared limit or has a NaN snap radius.</exception>
    /// <exception cref="InvalidOperationException">The call overlaps another call into the same pool.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="workspaces"/> is disposed.</exception>
    PathPlanRequest BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget,
        PathPlanWorkspaces workspaces);
}
