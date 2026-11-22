using System;
using System.Linq;
using System.Numerics;
using GridPlanningWorkspaces = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Constrained traversals can scan a footprint within a single search operation and plan
    /// synchronously. Unconstrained legacy and baked-graph planners support requests.</summary>
    internal bool SupportsRequests => _allowsTraversal is null;

    /// <summary>Region requests also require surface heights on every layer.</summary>
    internal bool SupportsRegionRequests => SupportsRequests && _hasRegionSurfaces;

    bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRegionRequests;

    PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
        => new GridPlanningWorkspaces(this, count, largestBudget);

    void IIncrementalRegionPathPlanner.ValidateWorkspaces(PathPlanWorkspaces workspaces, PathQueryBudget budget)
    {
        if (!SupportsRegionRequests)
            throw new ArgumentException("This planner does not support region requests.", nameof(workspaces));
        GridPlanningWorkspaces.Require(workspaces).ValidateRaw(this, budget);
    }

    PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces)
    {
        RequireRequestSupport();
        GridPlanningWorkspaces pool = GridPlanningWorkspaces.Require(workspaces);
        if (!ReferenceEquals(pool.Planner, this))
            throw new ArgumentException("The workspace pool belongs to another planner.", nameof(workspaces));
        return pool.BeginRaw(start, goal, agentRadius, budget);
    }

    /// <summary>Refuses the incremental door before it can invoke an unbudgeted traversal constraint.</summary>
    internal void RequireRequestSupport()
    {
        if (!SupportsRequests)
            throw new NotSupportedException("Traversal constraints can scan a footprint inside one search operation and require synchronous planning.");
    }

    /// <summary>Seeds prewarmed stamps near wrap, using the same pool and requests as ordinary creation.</summary>
    internal PathPlanWorkspaces CreateWorkspacesForTests(int count, PathQueryBudget largestBudget, int generation)
        => new GridPlanningWorkspaces(this, count, largestBudget, generation);

    int WorkspaceFrontierCapacity(int expansions, PathQueryBudget largestBudget)
    {
        if (expansions == 0) return 1; // The seed is enqueued even when the expansion cap is zero.

        int gridDegree = 0;
        foreach (NavGrid layer in _space.Layers)
            gridDegree = Math.Max(gridDegree, Math.Min(3, layer.Width) * Math.Min(3, layer.Height) - 1);

        // Closed nodes never expand again. Count all link entries, including parallel links that
        // can each improve the same target and leave duplicates in the open set.
        long links = _linkEdges.Values.Select(edges => edges.Count).OrderDescending()
            .Take(expansions).Sum(degree => (long)degree);

        // Each expansion removes one entry before testing its edges. A stale pop only frees space.
        long capacity = 1L + (long)expansions * Math.Max(0, gridDegree - 1) + links;
        if (capacity > Array.MaxLength)
            throw new ArgumentException("The largest query budget requires a frontier larger than an array.", nameof(largestBudget));
        return (int)capacity;
    }

    /// <summary>Prewarmed request storage with a packed list of the nonempty member layers.</summary>
    internal sealed class RequestScratch(int totalNodes, int layerCount, int frontierCapacity, int chainCapacity)
        : SearchScratch(totalNodes, layerCount, totalNodes, frontierCapacity, chainCapacity)
    {
        readonly int[] _candidateLayers = new int[layerCount];

        internal void StartMembership() => ClearMemberLists();

        internal void RecordCandidateLayer(int layer, int index) => _candidateLayers[index] = layer;

        internal int CandidateLayer(int index) => _candidateLayers[index];
    }
}
