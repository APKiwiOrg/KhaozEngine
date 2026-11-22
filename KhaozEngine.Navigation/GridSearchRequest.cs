using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Frozen raw region query. Only an advance by an owner holding a lease starts or resumes the stepper.</summary>
    internal sealed class GridSearchRequest(GridPlanningWorkspaces pool, Vector3 start, NavGoalRegion goal,
        float radius, PathQueryBudget queryBudget) : PathPlanRequest
    {
        RequestStepper _stepper;
        bool _started;

        internal int LeaseSlot { get; set; } = -1;
        internal LinkedListNode<GridSearchRequest>? WaitNode { get; set; }

        /// <summary>The next indivisible operation for a leased request, used to test exact quota cuts.</summary>
        internal SearchPhase NextPhaseForTests => State != PathPlanState.Pending ? SearchPhase.Done :
            _started ? _stepper.Phase :
            pool.Planner._space.Layers.Count == 1 ? SearchPhase.Snap : SearchPhase.Layer;

        public override bool StartsFrom(Vector3 feet)
        {
            if (SameBits(start.X, feet.X) && SameBits(start.Y, feet.Y) && SameBits(start.Z, feet.Z)) return true;
            var planner = pool.Planner;
            return Resolve(planner, start) is { } original && Resolve(planner, feet) == original;
        }

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
                if (!_started)
                {
                    _stepper = new RequestStepper(pool.Planner, pool.ScratchAt(LeaseSlot), start, goal, radius, queryBudget);
                    _started = true;
                }

                int spent = 0;
                try
                {
                    _stepper.RunSlice(budget.Units, ref spent);
                }
                finally
                {
                    if (spent > 0) Spend(spent);
                }

                if (_stepper.Result is { } path) Complete(path);
            }
            catch
            {
                CancelPending();
                throw;
            }
        }

        /// <summary>Cancels under the pool's existing disposal guard.</summary>
        internal void CancelFromPool() => CancelPending();

        private protected override void Release()
        {
            pool.Release(this, LeaseSlot);
            LeaseSlot = -1;
            _stepper = default;
        }
    }
}
