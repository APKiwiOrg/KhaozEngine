using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Distinguishes an additional area refusal from a route already refused by the baked profile.</summary>
internal static class GroundPursuitAreaDiagnostic
{
    internal static GroundPursuitDetail Classify(GroundNavigation? navigation, Vector3 feet, NavGoalRegion goal,
        float radius, PathQueryBudget budget, GroundPursuitDetail detail)
    {
        if (navigation?.AreaExclusionSource is not { } source) return detail;
        // Only a validated walking route under the original policy witnesses an area-specific refusal.
        // Keep the same bounded query policy, and never steer or admit this diagnostic route.
        var planner = new RouteStraightener(source.Planner, source.Space, source.AllowsSegment);
        NavPath path = planner.FindPath(feet, goal, radius, budget);
        return GroundPursuitRoute.Validate(path, source.Space, goal, feet, source.AllowsSegment, out _) == GroundPursuitDetail.None
            ? GroundPursuitDetail.ExcludedArea : detail;
    }
}
