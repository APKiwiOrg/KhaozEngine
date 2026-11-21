using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// A region planner whose routes can also be planned in slices under a <see cref="PathWorkBudget"/>.
/// A request returns the same route as the synchronous <see cref="IRegionPathPlanner.FindPath(Vector3,
/// NavGoalRegion, float, PathQueryBudget)"/>.
/// </summary>
internal interface IIncrementalRegionPathPlanner : IRegionPathPlanner
{
    /// <summary>Creates a pool of <paramref name="count"/> search workspaces for this planner's requests,
    /// allocated and prewarmed now so no request allocates one later. See <see cref="PathPlanWorkspaces"/> for
    /// the ownership, owner sequence, grant, hand-over and release rules.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below one.</exception>
    PathPlanWorkspaces CreateWorkspaces(int count);

    /// <summary>
    /// Freezes <paramref name="start"/>, <paramref name="goal"/>, <paramref name="agentRadius"/> and
    /// <paramref name="budget"/> into a pending request and spends no work. The request takes a free workspace from
    /// <paramref name="workspaces"/> now when nothing waits, else it joins the pool's queue. The owner advances it
    /// in the same call. The goal's predicate must stay pure until the request is Ready or Cancelled.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="goal"/> or <paramref name="workspaces"/> is
    /// null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workspaces"/> belongs to another planner.</exception>
    /// <exception cref="InvalidOperationException">The call overlaps another call into the same pool.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="workspaces"/> is disposed.</exception>
    PathPlanRequest BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget,
        PathPlanWorkspaces workspaces);
}
