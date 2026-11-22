using System;
using System.Numerics;
using KhaozEngine.Navigation;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;
using GridSearchRequest = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigation
{
    /// <summary>Query-time footprint exclusions require synchronous planning, even with an unconstrained grid.
    /// A view's traversal constraint can scan a footprint within one grid search operation.</summary>
    internal bool SupportsRequests => ((ProfilePlanner)Planner).SupportsRequests;

    /// <summary>Prewarms all buffers for the profile's incremental planner. Area excluding views plan
    /// synchronously, as their grid search constraint can scan an entire footprint in one operation.</summary>
    /// <exception cref="ArgumentException">The supplied straightener is bound to a different profile or has no profile.</exception>
    /// <exception cref="NotSupportedException">This profile requires synchronous planning.</exception>
    internal PathPlanWorkspaces CreatePlanningWorkspaces(int count, PathQueryBudget largestBudget,
        RouteStraightener? straightener = null)
        => ((ProfilePlanner)Planner).CreateWorkspaces(count, largestBudget, straightener);

    /// <summary>Freezes a region plan whose start admission, grid search and optional straightening share one lease.
    /// Area excluding views require synchronous planning to keep whole-footprint constraints outside the quota.</summary>
    /// <exception cref="ArgumentException">The straightener belongs to another profile or has no profile,
    /// or the pool does not belong to the straightener.</exception>
    /// <exception cref="NotSupportedException">This profile requires synchronous planning.</exception>
    internal PathPlanRequest BeginPlanningPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget,
        PathPlanWorkspaces workspaces, RouteStraightener straightener)
    {
        ArgumentNullException.ThrowIfNull(straightener);
        return ((ProfilePlanner)Planner).BeginPath(start, goal, radius, budget, workspaces, straightener);
    }

    /// <summary>Gets the grid behind this profile's request adapters.</summary>
    internal GridPathPlanner RequestGrid => ((IGridRequestAdapter)Planner).RequestGrid;

    private sealed partial class ProfilePlanner : IGridRequestAdapter
    {
        internal bool SupportsRequests => !navigation._footprint.HasAreaExclusions && planner.SupportsRequests;

        GridPathPlanner IGridRequestAdapter.RequestGrid
        {
            get
            {
                RequireRequestSupport();
                return planner;
            }
        }

        GridSearchRequest IGridRequestAdapter.CreateRequest(RequestAdmission admission)
            => new ProfilePathRequest(navigation, admission);

        void RequireRequestSupport()
        {
            if (!SupportsRequests)
                throw new NotSupportedException("This ground navigation profile requires synchronous planning.");
        }

        bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRequests;

        PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
            => CreateWorkspaces(count, largestBudget);

        internal PathPlanWorkspaces CreateWorkspaces(int count, PathQueryBudget largestBudget, RouteStraightener? straightener = null)
        {
            RequireStraightenerProfile(straightener);
            return new GridPlanningWorkspaces((IGridRequestAdapter?)straightener ?? this, count, largestBudget);
        }

        void RequireStraightenerProfile(RouteStraightener? straightener)
        {
            if (straightener is not null && !straightener.UsesNavigation(navigation))
                throw new ArgumentException("The straightener must be bound to this ground navigation profile.", nameof(straightener));
        }

        PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
            PathQueryBudget budget, PathPlanWorkspaces workspaces)
            => BeginPath(start, goal, agentRadius, budget, workspaces);

        internal PathPlanRequest BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget,
            PathPlanWorkspaces workspaces, RouteStraightener? straightener = null)
        {
            RequireStraightenerProfile(straightener);
            RequireRequestSupport();
            return GridPlanningWorkspaces.Require(workspaces)
                .BeginAdapted((IGridRequestAdapter?)straightener ?? this, start, goal, agentRadius, budget);
        }
    }

    /// <summary>Runs the profile's point footprint and resting-layer admission before the raw grid search.</summary>
    internal class ProfilePathRequest : GridSearchRequest
    {
        NavEndpointRequest _endpoint;

        internal ProfilePathRequest(GroundNavigation navigation, RequestAdmission admission)
            : this(navigation, admission, (IGridRequestAdapter)navigation.Planner)
        {
        }

        private protected ProfilePathRequest(RouteStraightener straightener, RequestAdmission admission)
            : this(straightener.RequestNavigation(), admission, straightener)
        {
        }

        private ProfilePathRequest(GroundNavigation navigation, RequestAdmission admission, IGridRequestAdapter adapter)
            : base(admission, adapter)
        {
            Navigation = navigation;
            _endpoint = new NavEndpointRequest(navigation, Start);
        }

        GroundNavigation Navigation { get; }

        internal NavSegmentOperation NextStartOperationForTests => State == PathPlanState.Pending && HasLease
            ? _endpoint.NextOperation : NavSegmentOperation.Done;

        private protected override bool AdmitsStartChange(Vector3 feet)
            => Navigation.Resolve(Start, out _) && Navigation.Resolve(feet, out _);

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
