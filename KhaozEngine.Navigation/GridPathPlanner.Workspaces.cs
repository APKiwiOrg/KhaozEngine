using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    PathPlanWorkspaces IIncrementalRegionPathPlanner.CreateWorkspaces(int count) => new GridPlanningWorkspaces(this, count);

    PathPlanRequest IIncrementalRegionPathPlanner.BeginPath(Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, PathPlanWorkspaces workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (workspaces is not GridPlanningWorkspaces pool || !ReferenceEquals(pool.Planner, this))
            throw new ArgumentException("The workspace pool belongs to another planner.", nameof(workspaces));
        return pool.Begin(start, goal, agentRadius, budget);
    }

    /// <summary>Seeds prewarmed stamps near wrap, using the same pool and requests as ordinary creation.</summary>
    internal PathPlanWorkspaces CreateWorkspacesForTests(int count, int generation) => new GridPlanningWorkspaces(this, count, generation);

    /// <summary>Fixed leases for one grid planner. Requests queue only when all workspaces are occupied.</summary>
    internal sealed class GridPlanningWorkspaces : PathPlanWorkspaces
    {
        readonly int _count;
        readonly LinkedList<GridSearchRequest> _waiters = new();
        readonly Stack<int> _free;
        SearchScratch[] _scratch;
        GridSearchRequest?[] _owners;
        int _inCall;
        long _waits;
        bool _disposed;

        internal GridPlanningWorkspaces(GridPathPlanner planner, int count, int generation = 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            Planner = planner;
            _count = count;
            _free = new Stack<int>(count);
            _scratch = new SearchScratch[count];
            _owners = new GridSearchRequest[count];
            for (int slot = 0; slot < count; slot++)
            {
                _scratch[slot] = new SearchScratch(planner._totalNodes, planner._space.Layers.Count);
                _scratch[slot].SeedGeneration(generation);
                _free.Push(slot);
            }
        }

        internal GridPathPlanner Planner { get; }

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

        internal PathPlanRequest Begin(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
        {
            using var call = new PoolCall(this);
            ObjectDisposedException.ThrowIf(_disposed, this);
            Planner.ValidateRegion(goal, radius);
            var request = new GridSearchRequest(this, start, goal, radius, budget);
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

        internal SearchScratch ScratchAt(int slot) => _scratch[slot];

        internal void Enter()
        {
            if (Interlocked.CompareExchange(ref _inCall, 1, 0) != 0)
                throw new InvalidOperationException("Calls into a path workspace pool must not overlap.");
        }

        internal void Exit() => Volatile.Write(ref _inCall, 0);

        internal void Release(GridSearchRequest request, int slot)
        {
            if (request.WaitNode is { } waiting)
            {
                _waiters.Remove(waiting);
                request.WaitNode = null;
                return;
            }

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
            _scratch = Array.Empty<SearchScratch>();
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
