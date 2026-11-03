using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    internal static MovementStepResult SettlePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
        MovementBoundary? boundary = null)
    {
        MovementStepResult validated = ValidatePlacement(state, tuning, water, queries, composition, boundary);
        if (validated.Outcome != MovementStepOutcome.Advanced) return validated;
        try
        {
            MoveState placed = MovementStepResult.HoldState(state.State);
            MovementSelection selection = validated.State.Selection!.Value;
            var contact = PlacementQueries(placed, tuning, composition, water, queries, selection, boundary);
            Vector3 feet = contact.KernelFeet(placed.Position, tuning);
            Vector3 candidate = placed.Position;
            bool supported = false;
            MovementContactSupportBinding? binding = null;
            if (placed.Grounded || placed.VerticalVelocity <= 0)
            {
                SupportSample support = ContactPlacement.Find(contact, feet, tuning, composition,
                    placed.Grounded ? tuning.StepHeight : tuning.GroundedEpsilon, tuning.GroundedEpsilon, out _);
                supported = ContactPlacement.TrySeat(contact, feet, support, tuning, composition, out Vector3 seated);
                if (supported)
                {
                    ContactPlacement.Validate(contact, seated, tuning, composition);
                    SupportSample final = ContactPlacement.Find(contact, seated, tuning, composition,
                        tuning.StepHeight, tuning.GroundedEpsilon, out binding);
                    if (final.Status != SupportStatus.Walkable || binding is null)
                        return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
                    candidate = contact.SupportedBodyCentre(seated, final);
                }
            }
            MovementWaterPoint point = contact.SampleMedium(contact.KernelFeet(candidate, tuning))!.Value;
            if (supported && binding!.Value.Space != point.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
            bool footing = supported && FootingAllowed(point, wasSwimming, candidate.Y, tuning, water);
            placed.Grounded = footing;
            if (footing)
            {
                placed.Position = candidate;
                placed.VerticalVelocity = 0;
                placed.TimeSinceGrounded = 0;
            }
            else if (supported)
            {
                point = contact.SampleMedium(feet)!.Value;
                if (point.Space != selection.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
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

    internal static MovementStepResult ValidatePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
        MovementBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!state.IsValid || !ValidCarriedState(state.State) || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        if (state.Frame != queries.Frame) return Hold(state, MovementStepOutcome.FrameMismatch);
        if (!MovementFrameRebinding.TryRebind(state, state.Frame, out _))
            return Hold(state, MovementStepOutcome.EnvironmentUnresolved);
        try
        {
            ShellGeometry.Validate(tuning, composition.Foot);
            var unselected = new FramedMovementState(state.State, state.Frame, null);
            MovementAvailability status = queries.RebuildSelection(unselected, out MovementSelection selection);
            if (status != MovementAvailability.Known) return Hold(state, Outcome(status));
            MovementBodyQuery body = Body(state.State.Position, tuning, selection);
            MovementStepOutcome placement = WaterPlacement(body, tuning, water, queries, boundary);
            if (placement != MovementStepOutcome.Advanced) return Hold(state, placement);
            var contact = PlacementQueries(state.State, tuning, composition, water, queries, selection, boundary);
            Vector3 feet = contact.KernelFeet(state.State.Position, tuning);
            ContactPlacement.Validate(contact, feet, tuning, composition);
            MovementWaterPoint point = contact.SampleMedium(feet)!.Value;
            if (point.Space != selection.Space) return Hold(state, MovementStepOutcome.EnvironmentInvalid);
            if (point.InWater && !point.Interval!.Value.UpperIsFreeSurface)
                return Hold(state, MovementStepOutcome.PlacementRefused);
            MovementContactSupportBinding? binding = null;
            if (state.State.Grounded)
            {
                SupportSample support = ContactPlacement.Find(contact, feet, tuning, composition, tuning.StepHeight,
                    tuning.GroundedEpsilon, out var found);
                bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
                if (support.Status == SupportStatus.Walkable &&
                    FootingAllowed(point, wasSwimming, state.State.Position.Y, tuning, water))
                {
                    _ = contact.SupportedBodyCentre(feet, support);
                    binding = found;
                }
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

    internal static LeasedContactQueryContext PlacementQueries(in MoveState state, in MoveTuning tuning,
        in ContactComposition composition, in WaterTraversalPolicy water, MovementQueryLease queries,
        in MovementSelection selection, MovementBoundary? boundary)
    {
        var contact = queries.Workspace.Queries;
        contact.Initialize(queries, tuning, selection,
            new(selection.Space, Body(state.Position, tuning, selection).Feet), composition, water.Mode, boundary);
        return contact;
    }
}
