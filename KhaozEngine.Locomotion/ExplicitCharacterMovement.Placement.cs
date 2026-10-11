using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    /// <summary>Classifies a placement without advancing time. A nearby eligible floor is reached
    /// only through shared contact placement. Use ValidatePlacement for an exact correction basis.</summary>
    public static MovementStepResult SettlePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        MovementStepResult validated = ValidatePlacement(state, tuning, water, queries, boundary);
        if (validated.Outcome != MovementStepOutcome.Advanced) return validated;
        try
        {
            MoveState placed = MovementStepResult.HoldState(state.State);
            MovementSelection selection = validated.State.Selection!.Value;
            var contact = PlacementQueries(placed, tuning, water, queries, selection, boundary);
            Vector3 feet = contact.KernelFeet(placed.Position, tuning);
            bool supported = false;
            MovementContactSupportBinding? binding = null;
            if (placed.Grounded || placed.VerticalVelocity <= 0)
            {
                SupportSample support = ContactPlacement.Find(contact, feet, tuning,
                    placed.Grounded ? tuning.StepHeight : tuning.GroundedEpsilon, tuning.GroundedEpsilon, out _);
                supported = ContactPlacement.TrySeat(contact, feet, support, tuning, out Vector3 seated);
                if (supported)
                {
                    ContactPlacement.Validate(contact, seated, tuning);
                    SupportSample final = ContactPlacement.Find(contact, seated, tuning,
                        tuning.StepHeight, tuning.GroundedEpsilon, out binding);
                    if (final.Status != SupportStatus.Walkable || binding is null)
                        return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
                    placed.Position = contact.BodyCentre(seated);
                }
            }
            MovementWaterPoint point = contact.SampleMedium(contact.KernelFeet(placed.Position, tuning))!.Value;
            if (supported && binding!.Value.Space != point.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
            bool footing = supported && FootingAllowed(point, wasSwimming, placed.Position.Y, tuning, water);
            placed.Grounded = footing;
            if (supported && placed.VerticalVelocity < 0) placed.VerticalVelocity = 0;
            if (footing)
            {
                placed.VerticalVelocity = 0;
                placed.TimeSinceGrounded = 0;
            }
            selection = new(point.Space, footing ? binding?.Owner : null, queries.Identity);
            ClassifyWater(ref placed, point, footing, wasSwimming, tuning, water, state.State.VerticalVelocity < 0);
            queries.AssertCurrent();
            return new(new(placed, state.Frame, selection), MovementStepOutcome.Advanced);
        }
        catch (ContactQueryFailure failure) { return Hold(state, failure.Outcome); }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }

    /// <summary>Rebuilds membership and validates the exact restored/corrected pose without running
    /// a tick or changing its carried state. Retain the lease through publication of a successful basis.</summary>
    public static MovementStepResult ValidatePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!state.IsValid || !ValidCarriedState(state.State) || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        if (state.Frame != queries.Frame) return Hold(state, MovementStepOutcome.FrameMismatch);
        if (!MovementFrameRebinding.TryRebind(state, state.Frame, out _))
            return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
        try
        {
            ContactShell.Validate(tuning);
            var unselected = new FramedMovementState(state.State, state.Frame, null);
            MovementAvailability status = queries.RebuildSelection(unselected, out MovementSelection selection);
            if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            MovementBodyQuery body = Body(state.State.Position, tuning, selection);
            MovementStepOutcome placement = WaterPlacement(body, tuning, water, queries, boundary);
            if (placement != MovementStepOutcome.Advanced) return Hold(state, placement);
            var contact = PlacementQueries(state.State, tuning, water, queries, selection, boundary);
            Vector3 feet = contact.KernelFeet(state.State.Position, tuning);
            ContactPlacement.Validate(contact, feet, tuning);
            MovementWaterPoint point = contact.SampleMedium(feet)!.Value;
            if (point.Space != selection.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            if (point.InWater && !point.Interval!.Value.UpperIsFreeSurface)
                return Hold(state, MovementStepOutcome.PlacementRefused);
            MovementContactSupportBinding? binding = null;
            if (state.State.Grounded)
            {
                SupportSample support = ContactPlacement.Find(contact, feet, tuning, tuning.StepHeight,
                    tuning.GroundedEpsilon, out var found);
                bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
                if (support.Status == SupportStatus.Walkable &&
                    FootingAllowed(point, wasSwimming, state.State.Position.Y, tuning, water)) binding = found;
                if (binding is { } owner && owner.Space != point.Space)
                    return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            }
            selection = new(point.Space, binding?.Owner, queries.Identity);
            queries.AssertCurrent();
            return new(new(state.State, state.Frame, selection), MovementStepOutcome.Advanced);
        }
        catch (ContactQueryFailure failure) { return Hold(state, failure.Outcome); }
        catch (InvalidOperationException) { return Hold(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(state, MovementStepOutcome.EnvironmentInvalid); }
    }

    static LeasedContactQueryContext PlacementQueries(in MoveState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, in MovementSelection selection,
        MovementBoundary? boundary) => new(queries, tuning, selection,
            new(selection.Space, Body(state.Position, tuning, selection).Feet), waterMode: water.Mode, boundary: boundary);
}
