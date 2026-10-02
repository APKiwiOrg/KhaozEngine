using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>Adds routes to reachable goal members while preserving the point query contract.</summary>
public interface IRegionPathPlanner : IPathPlanner
{
    /// <summary>
    /// Searches from <paramref name="start"/> to a passable cell-centre member of <paramref name="goal"/>
    /// within <paramref name="budget"/>. Candidates use actual surface feet heights. Complete ends
    /// inside the region. A snap cannot substitute a point outside the region. Heightless grids fail.
    /// </summary>
    NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget);
}
