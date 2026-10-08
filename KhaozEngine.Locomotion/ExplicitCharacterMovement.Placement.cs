using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    /// <summary>Classifies a placement without advancing time. A nearby eligible floor is reached
    /// only through the same combined resolver. Use ValidatePlacement for an exact correction basis.</summary>
    public static MovementStepResult SettlePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries)
    {
        MovementStepResult validated = ValidatePlacement(state, tuning, water, queries);
        if (validated.Outcome != MovementStepOutcome.Advanced) return validated;
        try
        {
            MoveState placed = MovementStepResult.HoldState(state.State);
            MovementSelection selection = validated.State.Selection!.Value;
            bool supported = false;
            if (placed.Grounded || placed.VerticalVelocity <= 0)
            {
                MovementAvailability status = Settle(ref placed, ref selection, tuning.GroundedEpsilon,
                    tuning, queries, out supported);
                if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            }
            placed.Grounded = supported;
            placed.Swimming = false;
            if (supported)
            {
                placed.VerticalVelocity = 0;
                placed.TimeSinceGrounded = 0;
                placed.WaterExcursion = WaterExcursionState.None;
            }
            else
            {
                selection = new(selection.Space, null, selection.Identity);
                if (placed.WaterExcursion == WaterExcursionState.Surface)
                    placed.WaterExcursion = WaterExcursionState.AirborneFromWater;
            }
            queries.AssertCurrent();
            return new(new(placed, state.Frame, selection), MovementStepOutcome.Advanced);
        }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }

    /// <summary>Rebuilds membership and validates the exact restored/corrected pose without running
    /// a tick or changing its carried state. Retain the lease through publication of a successful basis.</summary>
    public static MovementStepResult ValidatePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!state.IsValid || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        if (state.Frame != queries.Frame) return Hold(state, MovementStepOutcome.FrameMismatch);
        if (!MovementFrameRebinding.TryRebind(state, state.Frame, out _))
            return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
        try
        {
            var unselected = new FramedMovementState(state.State, state.Frame, null);
            MovementAvailability status = queries.RebuildSelection(unselected, out MovementSelection selection);
            if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            MovementBodyQuery body = Body(state.State.Position, tuning, selection);
            status = Move(body, Vector3.Zero, queries, out _, out _);
            if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            MovementWaterPoint point = queries.SampleCentreWater(body);
            if (point.Availability != MovementAvailability.Known) return Hold(state, Outcome(point.Availability));
            if (point.Space != selection.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            if (point.InWater) return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
            queries.AssertCurrent();
            return new(new(state.State, state.Frame, selection), MovementStepOutcome.Advanced);
        }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }
}
