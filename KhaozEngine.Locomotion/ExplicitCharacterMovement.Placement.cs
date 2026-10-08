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
                    tuning, water, queries, out supported);
                if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            }
            MovementWaterPoint point = queries.SampleCentreWater(Body(placed.Position, tuning, selection));
            if (point.Availability != MovementAvailability.Known) return Hold(state, Outcome(point.Availability));
            bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
            bool footing = supported && FootingAllowed(point, wasSwimming, placed.Position.Y, tuning, water);
            placed.Grounded = footing;
            if (supported && placed.VerticalVelocity < 0) placed.VerticalVelocity = 0;
            if (footing)
            {
                placed.VerticalVelocity = 0;
                placed.TimeSinceGrounded = 0;
            }
            else selection = new(selection.Space, null, selection.Identity);
            ClassifyWater(ref placed, point, footing, wasSwimming, tuning, water, state.State.VerticalVelocity < 0);
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
        if (!state.IsValid || !ValidCarriedState(state.State) || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
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
            MovementStepOutcome placement = WaterPlacement(body, tuning, water, queries);
            if (placement != MovementStepOutcome.Advanced) return Hold(state, placement);
            status = Move(body, Vector3.Zero, queries, water, tuning, out _, out _);
            if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            MovementWaterPoint point = queries.SampleCentreWater(body);
            if (point.Availability != MovementAvailability.Known) return Hold(state, Outcome(point.Availability));
            if (point.Space != selection.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            if (point.InWater && !point.Interval!.Value.UpperIsFreeSurface)
                return Hold(state, MovementStepOutcome.PlacementRefused);
            queries.AssertCurrent();
            return new(new(state.State, state.Frame, selection), MovementStepOutcome.Advanced);
        }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }
}
