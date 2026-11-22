using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    internal partial class GridSearchRequest
    {
        /// <summary>Fixed leases for one raw grid planner or the adapters approved by the pool owner. Requests wait when all leases are occupied.
        /// Nesting keeps slot and queue assignment private to the request and its pool.</summary>
        internal sealed class GridPlanningWorkspaces : PathPlanWorkspaces, IRequestPool
        {
            readonly IGridRequestAdapter? _adapter;
            readonly int _count;
            readonly PathQueryBudget _largestBudget;
            GridSearchRequest? _firstWaiter;
            GridSearchRequest? _lastWaiter;
            readonly Stack<int> _free;
            RequestScratch[] _scratch;
            GridSearchRequest?[] _owners;
            int _inCall;
            long _waits;
            bool _disposed;

            internal GridPlanningWorkspaces(GridPathPlanner planner, int count, PathQueryBudget largestBudget,
                int generation = 0)
            {
                planner.RequireRequestSupport();
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                if (float.IsNaN(largestBudget.SnapRadius))
                    throw new ArgumentOutOfRangeException(nameof(largestBudget), "The snap radius limit must be ordered.");

                Planner = planner;
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

            internal GridPlanningWorkspaces(IGridRequestAdapter adapter, int count, PathQueryBudget largestBudget)
                : this(adapter.RequestGrid, count, largestBudget)
                => _adapter = adapter;

            /// <summary>Requires the grid workspace implementation before either begin door checks ownership.</summary>
            internal static GridPlanningWorkspaces Require(PathPlanWorkspaces workspaces)
            {
                ArgumentNullException.ThrowIfNull(workspaces);
                if (workspaces is not GridPlanningWorkspaces pool)
                    throw new ArgumentException("The workspace pool belongs to another planner.", nameof(workspaces));
                return pool;
            }

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

            /// <summary>Checks that a raw planner can use the pool for the declared query.</summary>
            internal void ValidateRaw(GridPathPlanner planner, PathQueryBudget budget)
            {
                using var call = new PoolCall(this);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_adapter is not null || !ReferenceEquals(Planner, planner))
                    throw new ArgumentException("The workspace pool belongs to another planner.", "workspaces");
                ValidateBudget(budget);
            }

            /// <summary>Checks that this pool owner authorizes an adapter and that its query fits the pool.</summary>
            internal void ValidateAdapted(IGridRequestAdapter adapter, PathQueryBudget budget)
            {
                using var call = new PoolCall(this);
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireAdapter(adapter);
                ValidateBudget(budget);
            }

            void RequireAdapter(IGridRequestAdapter adapter)
            {
                ArgumentNullException.ThrowIfNull(adapter);
                if (_adapter?.OwnsWorkspaceRequestsFrom(adapter) != true)
                    throw new ArgumentException("The workspace pool belongs to another planner.", "workspaces");
            }

            void ValidateBudget(PathQueryBudget budget)
            {
                if (budget.MaxExpandedNodes > _largestBudget.MaxExpandedNodes || !(budget.SnapRadius <= _largestBudget.SnapRadius))
                    throw new ArgumentException("The query budget exceeds the workspace pool's declared limits.", nameof(budget));
            }

            /// <summary>Starts raw search only in a pool created for a raw grid planner.</summary>
            internal PathPlanRequest BeginRaw(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
            {
                using var call = new PoolCall(this);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_adapter is not null)
                    throw new ArgumentException("An adapter's workspace pool cannot begin raw grid search.", "workspaces");
                return Attach(new GridSearchRequest(RequestAdmission.Admit(this, start, goal, radius, budget)));
            }

            /// <summary>Starts an authorized adapter with the exact query admitted by this begin call.</summary>
            internal PathPlanRequest BeginAdapted(IGridRequestAdapter adapter, Vector3 start, NavGoalRegion goal, float radius,
                PathQueryBudget budget)
            {
                using var call = new PoolCall(this);
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireAdapter(adapter);

                RequestAdmission admission = RequestAdmission.Admit(this, start, goal, radius, budget);
                GridSearchRequest request = adapter.CreateRequest(admission);
                if (!ReferenceEquals(request._adapter, adapter) || !ReferenceEquals(request._admission, admission) ||
                    request.State != PathPlanState.Pending)
                    throw new ArgumentException("The adapter must create its own pending request for this admission.", nameof(adapter));

                request.OnBegin();
                if (request.State != PathPlanState.Pending || request._leaseSlot >= 0 || IsWaiting(request))
                    throw new InvalidOperationException("A begin hook must leave its request pending and detached.");

                return Attach(request);
            }

            PathPlanRequest Attach(GridSearchRequest request)
            {
                if (_firstWaiter is null && _free.TryPop(out int slot))
                {
                    Grant(request, slot);
                }
                else
                {
                    request._previousWaiter = _lastWaiter;
                    if (_lastWaiter is { } tail) tail._nextWaiter = request;
                    else _firstWaiter = request;
                    _lastWaiter = request;
                    _waits++;
                }
                return request;
            }

            void IRequestPool.RunCall(GridSearchRequest request, PathWorkBudget? budget)
            {
                using var call = new PoolCall(this);
                request.RunOwnedCall(budget);
            }

            void Enter()
            {
                if (Interlocked.CompareExchange(ref _inCall, 1, 0) != 0)
                    throw new InvalidOperationException("Calls into a path workspace pool must not overlap.");
            }

            void Exit() => Volatile.Write(ref _inCall, 0);

            /// <summary>Returns a lease only after the request has completed or been cancelled.</summary>
            void IRequestPool.Release(GridSearchRequest request)
            {
                if (request.State == PathPlanState.Pending || !ReferenceEquals(request._admission.Pool, this))
                    throw new ArgumentException("Only a finished request from this pool can release a lease.", nameof(request));

                if (IsWaiting(request))
                {
                    RemoveWaiter(request);
                    return;
                }

                int slot = request._leaseSlot;
                if (slot < 0) return;
                if (!ReferenceEquals(_owners[slot], request))
                    throw new ArgumentException("This request does not hold the workspace lease.", nameof(request));

                _owners[slot] = null;
                if (_disposed) return;
                if (_firstWaiter is { } next)
                {
                    RemoveWaiter(next);
                    Grant(next, slot);
                }
                else
                    _free.Push(slot);
            }

            // Keep both links on the request so a queued begin allocates the same as a leased begin.
            bool IsWaiting(GridSearchRequest request) => request._previousWaiter is not null || ReferenceEquals(_firstWaiter, request);

            void RemoveWaiter(GridSearchRequest request)
            {
                GridSearchRequest? previous = request._previousWaiter, next = request._nextWaiter;
                if (previous is not null) previous._nextWaiter = next;
                else _firstWaiter = next;
                if (next is not null) next._previousWaiter = previous;
                else _lastWaiter = previous;
                request._previousWaiter = null;
                request._nextWaiter = null;
            }

            void Grant(GridSearchRequest request, int slot)
            {
                _owners[slot] = request;
                request._leaseSlot = slot;
                request._leasedScratch = _scratch[slot];
            }

            public override void Dispose()
            {
                using var call = new PoolCall(this);
                if (_disposed) return;
                _disposed = true;
                while (_firstWaiter is { } next) next.CancelPending();
                foreach (GridSearchRequest? owner in _owners) owner?.CancelPending();
                _free.Clear();
                _owners = Array.Empty<GridSearchRequest>();
                _scratch = Array.Empty<RequestScratch>();
            }

            /// <summary>One validated query for this pool. A request can only read the admitted inputs.</summary>
            internal sealed class RequestAdmission
            {
                private RequestAdmission(GridPlanningWorkspaces pool, Vector3 start, NavGoalRegion goal, float radius,
                    PathQueryBudget budget)
                {
                    Pool = pool;
                    Start = start;
                    Goal = goal;
                    Radius = radius;
                    Budget = budget;
                }

                internal GridPlanningWorkspaces Pool { get; }
                internal Vector3 Start { get; }
                internal NavGoalRegion Goal { get; }
                internal float Radius { get; }
                internal PathQueryBudget Budget { get; }

                internal static RequestAdmission Admit(GridPlanningWorkspaces pool, Vector3 start, NavGoalRegion goal,
                    float radius, PathQueryBudget budget)
                {
                    pool.Planner.ValidateRegion(goal, radius);
                    pool.ValidateBudget(budget);
                    return new RequestAdmission(pool, start, goal, radius, budget);
                }
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
}
