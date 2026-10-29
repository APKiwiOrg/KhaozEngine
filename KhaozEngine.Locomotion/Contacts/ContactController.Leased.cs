using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

internal static partial class ContactController
{
    /// <summary>Borrows the caller's composed interval. Land uses the shared whole tick, with geometric feet
    /// and the selected composition. Swimming hands off accepted substeps within the same tick.</summary>
    internal static MovementStepResult StepLeased(in FramedMovementState state, Vector2 moveDir,
        float speedFraction, bool run, bool jump, float dt, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementQueryLease queries, MovementBoundary? boundary = null, float? faceYaw = null,
        GroundCoreSettings? settings = null) =>
        StepLeased(state, moveDir, speedFraction, run, jump, dt, tuning, water, queries,
            new ContactComposition(LeasedContactQueryContext.PlacementSkin,
                settings ?? new GroundCoreSettings(tuning.FootRadiusFraction)), boundary, faceYaw);

    internal static MovementStepResult StepLeased(in FramedMovementState state, Vector2 moveDir,
        float speedFraction, bool run, bool jump, float dt, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementQueryLease queries, in ContactComposition composition, MovementBoundary? boundary = null,
        float? faceYaw = null)
    {
        using var entry = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.ControllerEntry);
        ArgumentNullException.ThrowIfNull(queries);
        if (!state.IsValid || !ExplicitCharacterMovement.Valid(tuning) ||
            !ExplicitCharacterMovement.ValidCarriedState(state.State) || !water.IsValid ||
            water.Mode == WaterTraversalMode.Legacy || !float.IsFinite(dt) || dt <= 0 ||
            !float.IsFinite(speedFraction) || speedFraction < 0 ||
            !float.IsFinite(moveDir.X) || !float.IsFinite(moveDir.Y) ||
            faceYaw is { } yaw && !float.IsFinite(yaw))
            return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
        if (state.Frame != queries.Frame)
            return HoldLeased(state, MovementStepOutcome.FrameMismatch);
        if (!MovementFrameRebinding.TryRebind(state, state.Frame, out _))
            return HoldLeased(state, MovementStepOutcome.EnvironmentUnresolved);
        bool beganTick = false;
        try
        {
            queries.AssertCurrent();
            queries.BeginContactTick();
            beganTick = true;
            GroundCoreSettings foot = composition.Foot;
            ShellGeometry.Validate(tuning, foot);
            MovementSelection selection = default;
            if (state.Selection is { } selected)
            {
                if (selected.Identity != queries.Identity ||
                    !string.Equals(selected.Space.WorldId, queries.Witness.Scope.WorldId, StringComparison.Ordinal) ||
                    queries.Witness.Scope.CurrentSpace is { } hint && hint != selected.Space ||
                    !state.State.Grounded && selected.Support.HasValue)
                    return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
                selection = selected;
            }
            // A carried key does not establish readiness for a fresh null-hint interval.
            if (state.Selection is null || queries.RequiresSelectionReconstruction)
            {
                MovementAvailability status = queries.RebuildSelection(state, out selection);
                if (status != MovementAvailability.Known)
                    return HoldLeased(state, ExplicitCharacterMovement.Outcome(status));
            }

            var body = new MovementBodyQuery(state.State.Position, tuning.CapsuleRadius,
                tuning.CapsuleHalfHeight, selection.Space, selection.Support);
            MovementStepOutcome placement;
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Placement))
                placement = ExplicitCharacterMovement.WaterPlacement(body, tuning, water, queries, boundary);
            if (placement != MovementStepOutcome.Advanced) return HoldLeased(state, placement);
            var leased = queries.Workspace.Queries;
            leased.Initialize(queries, tuning, selection,
                new(selection.Space, body.Feet), composition, waterMode: water.Mode, boundary: boundary);
            Vector3 feet = leased.KernelFeet(state.State.Position, tuning);
            MovementWaterPoint point = leased.SampleMedium(feet)!.Value;
            if (point.Space != selection.Space)
                return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
            MoveState start = ClassifyStartWater(state.State, point, feet, tuning, water, leased, foot);
            if (start.Commitment.IsActive) start.JumpBufferRemaining = 0;
            var modes = queries.Workspace.Modes;
            modes.Initialize(leased, start, tuning, water, jump);
            MoveState next = StepWithQueries(start, moveDir, speedFraction, run, jump, dt, tuning,
                leased, out ContactTickContinuation continuation, faceYaw: faceYaw, settings: foot, fluid: modes);
            if (!continuation.Completed)
                return HoldLeased(state, leased.Outcome == MovementStepOutcome.Advanced
                    ? MovementStepOutcome.EnvironmentUnresolved : leased.Outcome);
            if (leased.Outcome is not (MovementStepOutcome.Advanced or MovementStepOutcome.Blocked) || leased.RecoveryDenied)
                return HoldLeased(state, leased.Outcome);
            Vector3 endFeet = leased.KernelFeet(next.Position, tuning);
            MovementWaterPoint destination = leased.SampleMedium(endFeet)!.Value;
            MovementContactSupportBinding? binding = continuation.Binding;
            if (next.Grounded)
            {
                SupportSample support = continuation.Support;
                if (support.Status is not (SupportStatus.Walkable or SupportStatus.Steep) || binding is null ||
                    binding.Value.Space != destination.Space)
                    throw new ContactQueryFailure(MovementAvailability.Unresolved);
            }
            if (next.Swimming || next.WaterExcursion == WaterExcursionState.AirborneFromWater)
            {
                next.JumpBufferRemaining = 0;
                next.StepDeltaY = next.ClimbRate = next.ClimbRateEwma = 0;
            }
            var framed = new FramedMovementState(next, state.Frame,
                new(destination.Space, next.Grounded ? binding?.Owner : null, queries.Identity));
            if (!MovementFrameRebinding.TryRebind(framed, framed.Frame, out _))
                return HoldLeased(state, MovementStepOutcome.EnvironmentUnresolved);
            queries.AssertCurrent();
            return new(framed, leased.Outcome)
            {
                ContactEnteredSwimming = modes.EnteredWater,
                ContactAirLanded = continuation.AirLanded,
                ContactWalkableSupport = next.Grounded && continuation.Support.Status == SupportStatus.Walkable,
                ContactUnwalkableSupport = continuation.UnwalkableSupport,
                ContactFirstAirLandingFeet = continuation.FirstAirLandingFeet,
            };
        }
        catch (ContactQueryFailure failure) { return HoldLeased(state, failure.Outcome); }
        catch (InvalidOperationException) { return HoldLeased(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid); }
        finally
        {
            if (beganTick) queries.EndContactTick();
        }
    }

    static MovementStepResult HoldLeased(in FramedMovementState state, MovementStepOutcome outcome)
    {
        if (!state.IsValid) return new(state, outcome);
        var held = new FramedMovementState(state.State with { ClimbRateEwma = 0 }, state.Frame, state.Selection);
        return ExplicitCharacterMovement.Hold(held, outcome);
    }
}
