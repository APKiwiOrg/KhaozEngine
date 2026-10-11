using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal static partial class ContactController
{
    /// <summary>Borrows the caller's composed interval. Land uses the shared whole tick, with geometric feet
    /// and the explicit placement profile. Swimming keeps its existing mode until its continuation is adopted.</summary>
    internal static MovementStepResult StepLeased(in FramedMovementState state, Vector2 moveDir,
        float speedFraction, bool run, bool jump, float dt, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementQueryLease queries, MovementBoundary? boundary = null, float? faceYaw = null,
        GroundCoreSettings? settings = null)
    {
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
        try
        {
            queries.AssertCurrent();
            ContactShell.Validate(tuning);
            GroundCoreSettings foot = settings ?? new GroundCoreSettings();
            if (!float.IsFinite(foot.FootRadiusFraction) || foot.FootRadiusFraction <= 0)
                return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
            MovementSelection selection;
            if (state.Selection is { } selected)
            {
                if (selected.Identity != queries.Identity ||
                    queries.Witness.Scope.CurrentSpace is { } hint && hint != selected.Space ||
                    !state.State.Grounded && selected.Support.HasValue)
                    return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
                selection = selected;
            }
            else
            {
                MovementAvailability status = queries.RebuildSelection(state, out selection);
                if (status != MovementAvailability.Known)
                    return HoldLeased(state, ExplicitCharacterMovement.Outcome(status));
            }

            var body = new MovementBodyQuery(state.State.Position, tuning.CapsuleRadius,
                tuning.CapsuleHalfHeight, selection.Space, selection.Support);
            MovementStepOutcome placement = ExplicitCharacterMovement.WaterPlacement(body, tuning, water, queries, boundary);
            if (placement != MovementStepOutcome.Advanced) return HoldLeased(state, placement);
            var leased = new LeasedContactQueryContext(queries, tuning, selection,
                new(selection.Space, body.Feet), waterMode: water.Mode, boundary: boundary);
            Vector3 feet = leased.KernelFeet(state.State.Position, tuning);
            MovementWaterPoint point = leased.SampleMedium(feet)!.Value;
            if (point.Space != selection.Space)
                return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid);
            bool wasSwimming = state.State.Swimming || state.State.WaterExcursion == WaterExcursionState.Surface;
            float gate = CharacterMovement.TractionGate(state.State.Grounded, tuning);
            SupportSample initial = leased.FindSupport(new(new(feet.X, feet.Z), feet.Y,
                foot.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight, MathF.Cos(gate)));
            bool footingAllowed = ExplicitCharacterMovement.FootingAllowed(point, wasSwimming,
                state.State.Position.Y, tuning, water);
            bool supported = initial.Status == SupportStatus.Walkable && footingAllowed &&
                (state.State.Grounded || state.State.VerticalVelocity <= 0 &&
                    MathF.Abs(initial.Height - feet.Y) <= tuning.GroundedEpsilon);
            MoveState start = state.State;
            ExplicitCharacterMovement.ClassifyWater(ref start, point, supported, wasSwimming,
                tuning, water, start.VerticalVelocity < 0);
            if (water.Mode == WaterTraversalMode.SurfaceSwimmer &&
                (start.Swimming || start.WaterExcursion == WaterExcursionState.AirborneFromWater ||
                    point.InWater && initial.Status != SupportStatus.Walkable))
                return ExplicitCharacterMovement.StepFluid(state, moveDir, speedFraction, run, jump,
                    faceYaw, dt, tuning, water, queries, boundary);
            start.Swimming = false;
            start.WaterExcursion = WaterExcursionState.None;
            if (supported && state.State.Swimming)
            {
                start.Grounded = true;
                start.VerticalVelocity = 0;
            }
            if (start.Commitment.IsActive) start.JumpBufferRemaining = 0;
            float mediumSpeed = ExplicitCharacterMovement.MediumSpeed(point, start.Position.Y, false, tuning);
            MoveTuning drive = tuning with
            {
                WalkSpeed = tuning.WalkSpeed * mediumSpeed,
                RunSpeed = tuning.RunSpeed * mediumSpeed,
            };
            MoveState next = StepWithQueries(start, moveDir, speedFraction, run, jump, dt, drive,
                leased, faceYaw: faceYaw, settings: foot);
            if (leased.Outcome is not (MovementStepOutcome.Advanced or MovementStepOutcome.Blocked) || leased.RecoveryDenied)
                return HoldLeased(state, leased.Outcome);
            Vector3 endFeet = leased.KernelFeet(next.Position, tuning);
            MovementWaterPoint destination = leased.SampleMedium(endFeet)!.Value;
            MovementContactSupportBinding? binding = null;
            if (next.Grounded)
            {
                SupportSample support = leased.FindSupport(new(new(endFeet.X, endFeet.Z), endFeet.Y,
                    foot.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight,
                    MathF.Cos(gate)), out binding);
                if (support.Status is not (SupportStatus.Walkable or SupportStatus.Steep) || binding is null ||
                    binding.Value.Space != destination.Space)
                    throw new ContactQueryFailure(MovementAvailability.Unresolved);
            }
            ExplicitCharacterMovement.ClassifyWater(ref next, destination, next.Grounded, wasSwimming,
                tuning, water, next.VerticalVelocity < 0);
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
            return new(framed, leased.Outcome);
        }
        catch (ContactQueryFailure failure) { return HoldLeased(state, failure.Outcome); }
        catch (InvalidOperationException) { return HoldLeased(state, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return HoldLeased(state, MovementStepOutcome.EnvironmentInvalid); }
    }

    static MovementStepResult HoldLeased(in FramedMovementState state, MovementStepOutcome outcome)
    {
        if (!state.IsValid) return new(state, outcome);
        var held = new FramedMovementState(state.State with { ClimbRateEwma = 0 }, state.Frame, state.Selection);
        return ExplicitCharacterMovement.Hold(held, outcome);
    }
}
