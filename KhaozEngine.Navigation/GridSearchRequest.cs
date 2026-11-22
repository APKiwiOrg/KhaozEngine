using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Frozen raw region query. Only an advance by an owner holding a lease starts or resumes the stepper.</summary>
    internal class GridSearchRequest(GridPlanningWorkspaces pool, Vector3 start, NavGoalRegion goal,
        float radius, PathQueryBudget queryBudget) : PathPlanRequest
    {
        RequestStepper _stepper;
        bool _started;

        internal int LeaseSlot { get; set; } = -1;
        internal LinkedListNode<GridSearchRequest>? WaitNode { get; set; }

        /// <summary>Reports whether the stepper holds the staged result array before publication.</summary>
        internal bool HasStagedResultArrayForTests => _stepper.HasStagedResultArrayForTests;

        /// <summary>Predicts the next paid operation while this request holds a lease.
        /// Done means no work is due, including a pending request that will publish with zero work.</summary>
        internal SearchPhase NextPhaseForTests
        {
            get
            {
                if (State != PathPlanState.Pending || LeaseSlot < 0) return SearchPhase.Done;
                if (_started) return _stepper.Phase;

                GridPathPlanner planner = pool.Planner;
                if (planner._space.Layers.Count > 1) return SearchPhase.Layer;
                return planner._traversal is null && SnapRingLimit(planner._space.Layers[0], queryBudget.SnapRadius) < 0
                    ? SearchPhase.Done : SearchPhase.Snap;
            }
        }

        /// <summary>The query feet supplied to both the raw search and its profile stages.</summary>
        private protected Vector3 Start => start;

        /// <summary>The raw search's selected start layer, available once it returns a nonempty route.</summary>
        private protected int StartLayer => _stepper.StartLayer;

        /// <summary>The scratch remains owned until the final result has been published or the request is cancelled.</summary>
        private protected RequestScratch Scratch => pool.ScratchAt(LeaseSlot);

        public override bool StartsFrom(Vector3 feet)
        {
            if (SameBits(start.X, feet.X) && SameBits(start.Y, feet.Y) && SameBits(start.Z, feet.Z)) return true;
            var planner = pool.Planner;
            return AdmitsStartChange(feet) && Resolve(planner, start) is { } original && Resolve(planner, feet) == original;
        }

        /// <summary>Checks profile start admission before comparing two different feet against the grid start.</summary>
        private protected virtual bool AdmitsStartChange(Vector3 feet) => true;

        (int Layer, int X, int Z)? Resolve(GridPathPlanner planner, Vector3 feet)
        {
            int layer = planner._space.LayerAt(feet);
            NavGrid grid = planner._space.Layers[layer];
            Vector2? snap = planner.SnapToPassable(layer, grid, new Vector2(feet.X, feet.Z), radius,
                queryBudget.SnapRadius, out _);
            if (snap is not Vector2 position) return null;
            (int x, int z) = grid.CellOf(position.X, position.Y);
            return (layer, x, z);
        }

        static bool SameBits(float first, float second)
            => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);

        private protected override void EnterCall() => pool.Enter();

        private protected override void ExitCall() => pool.Exit();

        private protected override void AdvanceCore(PathWorkBudget budget)
        {
            if (LeaseSlot < 0) return;
            try
            {
                int spent = 0;
                NavPath? path;
                try
                {
                    path = RunSlice(budget.Units, ref spent);
                }
                finally
                {
                    if (spent > 0) Spend(spent);
                }

                if (path is not null) Complete(path);
            }
            catch
            {
                CancelPending();
                throw;
            }
        }

        /// <summary>Runs the search with any prior effort in this advance included in <paramref name="spent"/>.
        /// Profile requests may do work before search and hold its result while preparing their final route.</summary>
        private protected virtual NavPath? RunSlice(int quota, ref int spent)
        {
            if (!_started)
            {
                _stepper = new RequestStepper(pool.Planner, Scratch, start, goal, radius, queryBudget);
                _started = true;
            }

            _stepper.RunSlice(quota, ref spent);
            return _stepper.Result;
        }

        /// <summary>Cancels under the pool's existing disposal guard.</summary>
        internal void CancelFromPool() => CancelPending();

        private protected override void Release()
        {
            pool.Release(this, LeaseSlot);
            LeaseSlot = -1;
            _stepper = default;
            ClearProfileStages();
        }

        /// <summary>Discards any cursor references to the released workspace and unpublished route.</summary>
        private protected virtual void ClearProfileStages() { }
    }
}
