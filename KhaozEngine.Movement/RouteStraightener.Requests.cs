using System;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class RouteStraightener
{
    private readonly GroundNavigation? _navigation;

    /// <summary>Uses the profile's segment and planner rules. Only unconstrained profiles can plan in slices.</summary>
    internal RouteStraightener(GroundNavigation navigation)
        : this((navigation ?? throw new ArgumentNullException(nameof(navigation))).Planner, navigation.Space, navigation.AllowsSegment)
        => _navigation = navigation;

    /// <summary>True when the profile can account for every operation. Area excluding views plan synchronously
    /// because their grid traversal constraint can scan a footprint in one search operation.</summary>
    internal bool SupportsRequests => _navigation?.SupportsRequests == true;

    bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRequests;

    PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
        => RequestNavigation().CreatePlanningWorkspaces(count, largestBudget, this);

    PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces)
        => RequestNavigation().BeginPlanningPath(start, goal, agentRadius, budget, workspaces, this);

    /// <summary>Checks whether this straightener uses the exact profile that receives the request.</summary>
    internal bool UsesNavigation(GroundNavigation navigation) => ReferenceEquals(_navigation, navigation);

    /// <summary>Gets the bound profile for straightening. Its planner separately checks request support.</summary>
    internal GroundNavigation RequestNavigation()
        => _navigation ?? throw new NotSupportedException("Route requests require a ground navigation profile with budgeted segment admission.");
}
