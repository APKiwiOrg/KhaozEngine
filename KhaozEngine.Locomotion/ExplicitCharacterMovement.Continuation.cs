using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    /// <summary>Rebuilds destination membership for a live continuation without moving the pose,
    /// advancing time or consuming an already accepted buffered press. Footing and water mode are
    /// reclassified from the destination facts. Restore and teleport use SettlePlacement instead.</summary>
    public static MovementStepResult ReclassifyContinuation(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        MovementStepResult validated = ValidatePlacement(state, tuning, water, queries, boundary);
        if (validated.Outcome != MovementStepOutcome.Advanced) return validated;
        try
        {
            MoveState carried = state.State;
            MovementSelection selection = validated.State.Selection!.Value;
            MovementBodyQuery body = Body(carried.Position, tuning, selection);
            MovementWaterPoint point = queries.SampleCentreWater(body);
            if (point.Availability != MovementAvailability.Known) return Hold(state, Outcome(point.Availability));
            bool wasSwimming = carried.Swimming || carried.WaterExcursion == WaterExcursionState.Surface;
            bool footing = false;
            MovementSupportKey? owner = null;
            if ((carried.Grounded || carried.VerticalVelocity <= 0) &&
                FootingAllowed(point, wasSwimming, carried.Position.Y, tuning, water))
            {
                var request = new MovementSupportRequest(body, 0, tuning.GroundedEpsilon, tuning.MaxSlopeRadians,
                    new(selection.Space, body.Feet));
                MovementAvailability status = MovementSupportResolver.Select(request, queries, out var support);
                if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
                if (support is { } candidate)
                {
                    status = Move(body, candidate.Centre - body.Centre, queries, water, tuning, boundary, out Vector3 end, out _);
                    if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
                    footing = end == candidate.Centre;
                    if (footing) owner = candidate.Candidate.Owner;
                }
            }
            carried.Grounded = footing;
            ClassifyWater(ref carried, point, footing, wasSwimming, tuning, water, carried.VerticalVelocity < 0);
            if (carried.Swimming || carried.WaterExcursion == WaterExcursionState.AirborneFromWater || point.InWater && !footing)
                carried.JumpBufferRemaining = 0;
            queries.AssertCurrent();
            return new(new(carried, state.Frame, new(selection.Space, owner, queries.Identity)), MovementStepOutcome.Advanced);
        }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }
}
