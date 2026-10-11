using System;
using KhaozEngine.Locomotion.Contacts;

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
            var contact = PlacementQueries(carried, tuning, water, queries, selection, boundary);
            var feet = contact.KernelFeet(carried.Position, tuning);
            MovementWaterPoint point = contact.SampleMedium(feet)!.Value;
            bool wasSwimming = carried.Swimming || carried.WaterExcursion == WaterExcursionState.Surface;
            bool footing = false;
            MovementSupportKey? owner = null;
            if ((carried.Grounded || carried.VerticalVelocity <= 0) &&
                FootingAllowed(point, wasSwimming, carried.Position.Y, tuning, water))
            {
                SupportSample support = ContactPlacement.Find(contact, feet, tuning,
                    carried.Grounded ? tuning.StepHeight : tuning.GroundedEpsilon, tuning.GroundedEpsilon, out var binding);
                footing = support.Status == SupportStatus.Walkable;
                if (footing)
                {
                    if (binding is null || binding.Value.Space != point.Space)
                        return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
                    owner = binding.Value.Owner;
                }
            }
            carried.Grounded = footing;
            ClassifyWater(ref carried, point, footing, wasSwimming, tuning, water, carried.VerticalVelocity < 0);
            if (carried.Swimming || carried.WaterExcursion == WaterExcursionState.AirborneFromWater || point.InWater && !footing)
                carried.JumpBufferRemaining = 0;
            queries.AssertCurrent();
            return new(new(carried, state.Frame, new(point.Space, owner, queries.Identity)), MovementStepOutcome.Advanced);
        }
        catch (ContactQueryFailure failure) { return Hold(state, failure.Outcome); }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }
}
