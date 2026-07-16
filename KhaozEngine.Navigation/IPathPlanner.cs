using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Seam for path queries over a <see cref="NavSpace"/>. <see cref="GridPathPlanner"/> is the grid A*
/// implementation, but callers should depend on this interface so a different planner can be swapped
/// in without touching call sites.
/// </summary>
public interface IPathPlanner
{
    /// <summary>
    /// Finds a route from <paramref name="start"/> to <paramref name="goal"/> (world positions, Y
    /// resolves each endpoint's layer) for an agent of <paramref name="agentRadius"/>, spending at
    /// most <paramref name="budget"/>'s search effort. Returns <see cref="NavPath.Unreachable"/> when
    /// either endpoint fails to snap onto a passable cell, or no route exists within budget.
    /// </summary>
    NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget);
}

/// <summary>
/// Convenience overload for <see cref="IPathPlanner"/> callers that do not need to tune the search
/// budget.
/// </summary>
public static class PathPlannerExtensions
{
    /// <summary>Finds a route using <see cref="PathQueryBudget.Default"/>.</summary>
    public static NavPath FindPath(this IPathPlanner planner, Vector3 start, Vector3 goal, float agentRadius)
        => planner.FindPath(start, goal, agentRadius, PathQueryBudget.Default);
}
