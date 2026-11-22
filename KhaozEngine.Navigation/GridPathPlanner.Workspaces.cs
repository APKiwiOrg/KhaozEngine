using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Constrained traversals can scan a footprint within a single search operation and plan
    /// synchronously. Unconstrained legacy and baked-graph planners support requests.</summary>
    internal bool SupportsRequests => _allowsTraversal is null;

    bool IIncrementalRegionPathPlanner.SupportsRequests => SupportsRequests;

    PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count, PathQueryBudget largestBudget)
        => CreateRequestWorkspaces(this, count, largestBudget);

    PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces)
        => BeginRequest(this, start, goal, agentRadius, budget, workspaces);

    /// <summary>Prewarms the grid workspace for its raw planner or one of its profile adapters.</summary>
    internal PathPlanWorkspaces CreateRequestWorkspaces(object owner, int count, PathQueryBudget largestBudget)
        => new GridPlanningWorkspaces(this, owner, count, largestBudget);

    /// <summary>Validates a query, then starts its grid request and any profile stages on the same lease.</summary>
    internal PathPlanRequest BeginRequest(object owner, Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces,
        Func<GridPlanningWorkspaces, GridSearchRequest>? createRequest = null)
    {
        RequireRequestSupport();
        ArgumentNullException.ThrowIfNull(workspaces);
        if (workspaces is not GridPlanningWorkspaces pool || !pool.IsOwnedBy(this, owner))
            throw new ArgumentException("The workspace pool belongs to another planner.", nameof(workspaces));
        return pool.Begin(start, goal, agentRadius, budget, createRequest);
    }

    /// <summary>Refuses the incremental door before it can invoke an unbudgeted traversal constraint.</summary>
    internal void RequireRequestSupport()
    {
        if (!SupportsRequests)
            throw new NotSupportedException("Traversal constraints can scan a footprint inside one search operation and require synchronous planning.");
    }

    /// <summary>Seeds prewarmed stamps near wrap, using the same pool and requests as ordinary creation.</summary>
    internal PathPlanWorkspaces CreateWorkspacesForTests(int count, PathQueryBudget largestBudget, int generation)
        => new GridPlanningWorkspaces(this, this, count, largestBudget, generation);

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

    /// <summary>Fixed leases for one grid planner. Requests queue only when all workspaces are occupied.</summary>
    internal sealed class GridPlanningWorkspaces : PathPlanWorkspaces
    {
        readonly int _count;
        readonly PathQueryBudget _largestBudget;
        readonly LinkedList<GridSearchRequest> _waiters = new();
        readonly Stack<int> _free;
        RequestScratch[] _scratch;
        GridSearchRequest?[] _owners;
        int _inCall;
        long _waits;
        bool _disposed;

        internal GridPlanningWorkspaces(GridPathPlanner planner, object owner, int count, PathQueryBudget largestBudget,
            int generation = 0)
        {
            planner.RequireRequestSupport();
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            if (float.IsNaN(largestBudget.SnapRadius))
                throw new ArgumentOutOfRangeException(nameof(largestBudget), "The snap radius limit must be ordered.");

            Planner = planner;
            PlannerOwner = owner;
            _count = count;
            _largestBudget = largestBudget;

            // Each reconstructed node was popped, and each candidate box covers at most its layer.
            int chainCapacity = Math.Clamp(largestBudget.MaxExpandedNodes, 0, planner._totalNodes);
            int frontierCapacity = planner.WorkspaceFrontierCapacity(chainCapacity, largestBudget);
            _free = new Stack<int>(count);
            _scratch = new RequestScratch[count];
            _owners = new GridSearchRequest[count];
            for (int slot = 0; slot < count; slot++)
            {
                _scratch[slot] = new RequestScratch(planner._totalNodes, planner._space.Layers.Count, frontierCapacity, chainCapacity);
                _scratch[slot].SeedGeneration(generation);
                _free.Push(slot);
            }
        }

        internal GridPathPlanner Planner { get; }

        object PlannerOwner { get; }

        /// <summary>Matches both the grid planner and the adapter that owns the pool.</summary>
        internal bool IsOwnedBy(GridPathPlanner planner, object owner)
            => ReferenceEquals(Planner, planner) && ReferenceEquals(PlannerOwner, owner);

        public override int Count
        {
            get
            {
                using var call = new PoolCall(this);
                return _count;
            }
        }

        public override long Waits
        {
            get
            {
                using var call = new PoolCall(this);
                return _waits;
            }
        }

        internal PathPlanRequest Begin(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget,
            Func<GridPlanningWorkspaces, GridSearchRequest>? createRequest)
        {
            using var call = new PoolCall(this);
            ObjectDisposedException.ThrowIf(_disposed, this);
            Planner.RequireRequestSupport();
            Planner.ValidateRegion(goal, radius);
            if (budget.MaxExpandedNodes > _largestBudget.MaxExpandedNodes ||
                !(budget.SnapRadius <= _largestBudget.SnapRadius))
                throw new ArgumentException("The query budget exceeds the workspace pool's declared limits.", nameof(budget));
            GridSearchRequest request = createRequest?.Invoke(this) ?? new GridSearchRequest(this, start, goal, radius, budget);
            if (!request.CanJoin(this))
                throw new ArgumentException("The factory must create a new pending request for this pool.", nameof(createRequest));
            if (_waiters.Count == 0 && _free.TryPop(out int slot))
            {
                Grant(request, slot);
            }
            else
            {
                request.WaitNode = _waiters.AddLast(request);
                _waits++;
            }
            return request;
        }

        internal RequestScratch ScratchAt(int slot) => _scratch[slot];

        internal void Enter()
        {
            if (Interlocked.CompareExchange(ref _inCall, 1, 0) != 0)
                throw new InvalidOperationException("Calls into a path workspace pool must not overlap.");
        }

        internal void Exit() => Volatile.Write(ref _inCall, 0);

        internal void Release(GridSearchRequest request)
        {
            if (request.WaitNode is { } waiting)
            {
                _waiters.Remove(waiting);
                request.WaitNode = null;
                return;
            }

            int slot = request.LeaseSlot;
            if (slot < 0) return;
            if (!ReferenceEquals(_owners[slot], request))
                throw new ArgumentException("This request does not hold the workspace lease.", nameof(request));

            _owners[slot] = null;
            if (_disposed) return;
            if (_waiters.First is { } next)
            {
                _waiters.RemoveFirst();
                next.Value.WaitNode = null;
                Grant(next.Value, slot);
            }
            else
                _free.Push(slot);
        }

        void Grant(GridSearchRequest request, int slot)
        {
            _owners[slot] = request;
            request.LeaseSlot = slot;
        }

        public override void Dispose()
        {
            using var call = new PoolCall(this);
            if (_disposed) return;
            _disposed = true;
            while (_waiters.First is { } next) next.Value.CancelFromPool();
            foreach (GridSearchRequest? owner in _owners) owner?.CancelFromPool();
            _free.Clear();
            _owners = Array.Empty<GridSearchRequest>();
            _scratch = Array.Empty<RequestScratch>();
        }

        readonly ref struct PoolCall
        {
            readonly GridPlanningWorkspaces _pool;

            internal PoolCall(GridPlanningWorkspaces pool)
            {
                pool.Enter();
                _pool = pool;
            }

            public void Dispose() => _pool.Exit();
        }
    }
}
