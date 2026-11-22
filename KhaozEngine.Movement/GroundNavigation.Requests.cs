using System;
using System.Numerics;
using KhaozEngine.Navigation;
using static KhaozEngine.Navigation.GridPathPlanner;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigation
{
    /// <summary>Area excluding views plan synchronously because a traversal constraint can scan a footprint
    /// within one grid search operation.</summary>
    internal bool SupportsRequests => ((ProfilePlanner)Planner).SupportsRequests;

    /// <summary>Prewarms all buffers for the profile's incremental planner. Area excluding views plan
    /// synchronously, as their grid search constraint can scan an entire footprint in one operation.</summary>
    /// <exception cref="NotSupportedException">This is an area excluding view.</exception>
    internal PathPlanWorkspaces CreatePlanningWorkspaces(int count, PathQueryBudget largestBudget,
        RouteStraightener? straightener = null)
        => ((ProfilePlanner)Planner).CreateWorkspaces(count, largestBudget, straightener);

    /// <summary>Freezes a region plan whose start admission, grid search and optional straightening share one lease.
    /// Area excluding views require synchronous planning to keep whole-footprint constraints outside the quota.</summary>
    /// <exception cref="NotSupportedException">This is an area excluding view.</exception>
    internal PathPlanRequest BeginPlanningPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget,
        PathPlanWorkspaces workspaces, RouteStraightener straightener)
        => ((ProfilePlanner)Planner).BeginPath(start, goal, radius, budget, workspaces, straightener);

    private sealed partial class ProfilePlanner
    {
        internal bool SupportsRequests => planner.SupportsRequests;

        bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRequests;

        PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
            => CreateWorkspaces(count, largestBudget);

        internal PathPlanWorkspaces CreateWorkspaces(int count, PathQueryBudget largestBudget, RouteStraightener? straightener = null)
            => planner.CreateRequestWorkspaces((object?)straightener ?? this, count, largestBudget);

        PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
            PathQueryBudget budget, PathPlanWorkspaces workspaces)
            => BeginPath(start, goal, agentRadius, budget, workspaces);

        internal PathPlanRequest BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget,
            PathPlanWorkspaces workspaces, RouteStraightener? straightener = null)
        {
            planner.RequireRequestSupport();
            ArgumentNullException.ThrowIfNull(goal);
            ValidateRadius(agentRadius);
            return planner.BeginRequest((object?)straightener ?? this, start, goal, agentRadius, budget, workspaces,
                pool => straightener is null
                    ? new ProfilePathRequest(navigation, pool, start, goal, agentRadius, budget)
                    : new RouteStraighteningRequest(straightener, navigation, pool, start, goal, agentRadius, budget));
        }
    }

    /// <summary>Runs the profile's point footprint and resting-layer admission before the raw grid search.</summary>
    internal class ProfilePathRequest(GroundNavigation navigation, GridPlanningWorkspaces pool, Vector3 start,
        NavGoalRegion goal, float radius, PathQueryBudget budget) : GridSearchRequest(pool, start, goal, radius, budget)
    {
        NavEndpointRequest _endpoint = new(navigation, start);

        private protected GroundNavigation Navigation => navigation;

        internal NavSegmentOperation NextStartOperationForTests => State == PathPlanState.Pending && LeaseSlot >= 0
            ? _endpoint.NextOperation : NavSegmentOperation.Done;

        private protected override bool AdmitsStartChange(Vector3 feet)
            => navigation.Resolve(Start, out _) && navigation.Resolve(feet, out _);

        private protected override NavPath? RunSlice(int quota, ref int spent)
        {
            while (_endpoint.NextOperation != NavSegmentOperation.Done)
            {
                int weight = NavSegmentRequest.WeightOf(_endpoint.NextOperation);
                if (spent != 0 && weight > quota - spent) return null;
                _endpoint.Step();
                spent += weight;
            }

            return _endpoint.Result is null ? NavPath.Unreachable : base.RunSlice(quota, ref spent);
        }
    }
}
