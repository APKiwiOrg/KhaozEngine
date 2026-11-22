using System;
using System.Numerics;
using KhaozEngine.Navigation;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;
using GridSearchRequest = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;

namespace KhaozEngine.Movement;

internal sealed partial class RouteStraightener : IGridRequestAdapter
{
    private readonly GroundNavigation? _navigation;

    /// <summary>Uses the profile's segment and planner rules. Only unconstrained profiles can plan in slices.</summary>
    internal RouteStraightener(GroundNavigation navigation)
        : this((navigation ?? throw new ArgumentNullException(nameof(navigation))).Planner, navigation.Space, navigation.AllowsSegment)
        => _navigation = navigation;

    /// <summary>True when the profile can account for every operation. Area excluding views plan synchronously
    /// because their grid traversal constraint can scan a footprint in one search operation.</summary>
    internal bool SupportsRequests => _navigation?.SupportsPlanningRequests == true;

    GridPathPlanner IGridRequestAdapter.RequestGrid => RequestNavigation().RequestGrid;

    GridSearchRequest IGridRequestAdapter.CreateRequest(RequestAdmission admission)
        => new RouteStraighteningRequest(this, admission);

    bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRequests;

    NavSpace IIncrementalRegionPathPlanner.Space => RequestNavigation().Space;

    PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
        => RequestNavigation().CreatePlanningWorkspaces(count, largestBudget, this);

    void IIncrementalRegionPathPlanner.ValidateWorkspaces(PathPlanWorkspaces workspaces, PathQueryBudget budget)
    {
        if (!SupportsRequests) throw new ArgumentException("This straightener requires synchronous queries.", nameof(workspaces));
        GridPlanningWorkspaces.Require(workspaces).ValidateAdapted(this, budget);
    }

    PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces)
        => RequestNavigation().BeginPlanningPath(start, goal, agentRadius, budget, workspaces, this);

    /// <summary>Checks whether this straightener uses the exact profile that receives the request.</summary>
    internal bool UsesNavigation(GroundNavigation navigation) => ReferenceEquals(_navigation, navigation);

    /// <summary>Gets the bound profile for straightening. Its planner separately checks request support.</summary>
    internal GroundNavigation RequestNavigation()
        => _navigation ?? throw new NotSupportedException("Route requests require a ground navigation profile with budgeted segment admission.");
}
