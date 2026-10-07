using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
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
