using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Explicit movement under a caller-owned read lease. The caller publishes the returned
/// pure state before releasing that lease. No physical mutation occurs in this kernel.</summary>
public static partial class ExplicitCharacterMovement
{
    public static MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries)
    {
        if (!float.IsFinite(command.Move.X) || !float.IsFinite(command.Move.Y) || !float.IsFinite(command.CameraYaw))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (direction, fraction, run) = CharacterMovement.ResolveCameraCommand(command, tuning);
        return StepCore(state, direction, fraction, run, command.Jump,
            command.FaceCamera ? command.CameraYaw : null, deltaSeconds, tuning, water, queries);
    }

    public static MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (resolved, fraction) = CharacterMovement.ResolveWorldDir(direction, preserveSmallMagnitude: true);
        return StepCore(state, resolved, fraction, run, false, null, deltaSeconds, tuning, water, queries);
    }

    static MovementStepResult StepCore(in FramedMovementState input, Vector2 direction, float fraction,
        bool run, bool jump, float? facing, float dt, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementQueryLease queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!input.IsValid || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy ||
            !float.IsFinite(dt) || dt <= 0 || !float.IsFinite(fraction) || !float.IsFinite(input.State.FacingYaw) ||
            !float.IsFinite(input.State.SpeedScale) || input.State.WaterExcursion > WaterExcursionState.AirborneFromWater ||
            input.State.Commitment.IsActive)
            return Hold(input, MovementStepOutcome.EnvironmentInvalid);
        if (input.Frame != queries.Frame) return Hold(input, MovementStepOutcome.FrameMismatch);
        if (!MovementFrameRebinding.TryRebind(input, input.Frame, out _))
            return Hold(input, MovementStepOutcome.EnvironmentUnresolved);
        try
        {
            queries.AssertCurrent();
            MovementSelection selection;
            MovementAvailability status;
            if (input.Selection is { } selected)
            {
                if (selected.Identity != queries.Identity ||
                    queries.Witness.Scope.CurrentSpace is { } hint && hint != selected.Space ||
                    !input.State.Grounded && selected.Support.HasValue)
                    return Hold(input, MovementStepOutcome.EnvironmentInvalid);
                selection = selected;
            }
            else
            {
                status = queries.RebuildSelection(input, out selection);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            }

            MoveState next = MovementStepResult.HoldState(input.State);
            next.JumpBufferRemaining = 0;
            MovementBodyQuery body = Body(next.Position, tuning, selection);
            // Even an idle tick needs complete initial placement and medium evidence.
            status = Move(body, Vector3.Zero, queries, out _, out _);
            if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            MovementWaterPoint point = queries.SampleCentreWater(body);
            if (point.Availability != MovementAvailability.Known) return Hold(input, Outcome(point.Availability));
            if (point.Space != selection.Space) return Hold(input, MovementStepOutcome.EnvironmentInvalid);

            bool supported = false;
            if (next.Grounded || next.VerticalVelocity <= 0)
            {
                status = Settle(ref next, ref selection, tuning.GroundedEpsilon, tuning, queries, out supported);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            }
            next.Grounded = supported;
            if (supported)
            {
                next.Swimming = false;
                next.WaterExcursion = WaterExcursionState.None;
                next.VerticalVelocity = 0;
                next.TimeSinceGrounded = 0;
            }
            else
            {
                selection = new(selection.Space, null, selection.Identity);
                if (next.WaterExcursion == WaterExcursionState.Surface)
                    next.WaterExcursion = WaterExcursionState.AirborneFromWater;
                next.Swimming = false;
            }

            // Wet policy is not inferred from SampleCentreWater. The initial complete trace above
            // currently refuses wet regions until their shared producer facts can certify admission.
            bool waterFlight = next.WaterExcursion == WaterExcursionState.AirborneFromWater;
            bool launched = jump && supported;
            float velocity = launched ? tuning.JumpSpeed : next.VerticalVelocity;
            if (!supported || launched)
                velocity = (float)Math.Max(-tuning.MaxFallSpeed, velocity - (double)tuning.Gravity * dt);
            float speed = (waterFlight ? tuning.SwimSpeed : run ? tuning.RunSpeed : tuning.WalkSpeed) *
                fraction * next.SpeedScale;
            Vector2 horizontal = direction * speed;
            if (!float.IsFinite(horizontal.X) || !float.IsFinite(horizontal.Y) || !float.IsFinite(velocity))
                return Hold(input, MovementStepOutcome.EnvironmentInvalid);
            Vector3 delta = new(horizontal.X * dt, velocity * dt, horizontal.Y * dt);
            Vector3 origin = next.Position;
            status = Move(Body(origin, tuning, selection), delta, queries, out Vector3 position, out bool blocked);
            if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            if (blocked && supported && !launched)
            {
                status = TryStepUp(Body(origin, tuning, selection), new(delta.X, delta.Z), tuning,
                    queries, out MovementSupportPlacement? step);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
                if (step is { } reached)
                {
                    position = reached.Centre;
                    selection = new(reached.Candidate.Space, reached.Candidate.Owner, queries.Identity);
                    blocked = false;
                    next.StepDeltaY = position.Y - origin.Y;
                }
            }
            next.Position = position;
            next.VerticalVelocity = velocity;
            next.Grounded = false;
            next.CommandedVelocity = horizontal;
            next.HorizontalVelocity = horizontal;
            next.FacingYaw = CharacterMovement.ResolveFacing(next.FacingYaw, direction, facing, dt, tuning);
            next.TimeSinceGrounded = launched ? tuning.CoyoteTime : input.State.TimeSinceGrounded + dt;
            next.SupportGranted = supported;
            selection = new(selection.Space, null, selection.Identity);

            if (velocity <= 0 && !launched)
            {
                float drop = supported ? tuning.StepHeight + tuning.GroundedEpsilon : tuning.GroundedEpsilon;
                status = Settle(ref next, ref selection, drop, tuning, queries, out bool landed);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
                if (landed)
                {
                    next.Grounded = true;
                    next.VerticalVelocity = 0;
                    next.TimeSinceGrounded = 0;
                    next.Swimming = false;
                    next.WaterExcursion = WaterExcursionState.None;
                    next.SupportGranted = true;
                    if (!input.State.Grounded) next.LandingImpactSpeed = Math.Max(0, -velocity);
                }
            }
            else if (blocked && position.Y < origin.Y + delta.Y)
                next.VerticalVelocity = 0;

            MovementWaterPoint destination = queries.SampleCentreWater(Body(next.Position, tuning, selection));
            if (destination.Availability != MovementAvailability.Known) return Hold(input, Outcome(destination.Availability));
            if (destination.InWater) return Hold(input, MovementStepOutcome.EnvironmentUnresolved);
            selection = new(destination.Space, next.Grounded ? selection.Support : null, queries.Identity);
            var framed = new FramedMovementState(next, input.Frame, selection);
            if (!MovementFrameRebinding.TryRebind(framed, framed.Frame, out _))
                return Hold(input, MovementStepOutcome.EnvironmentUnresolved);
            queries.AssertCurrent();
            return new(framed, blocked ? MovementStepOutcome.Blocked : MovementStepOutcome.Advanced);
        }
        catch (InvalidOperationException) { return Hold(input, MovementStepOutcome.EnvironmentUnresolved); }
        catch (ArgumentException) { return Hold(input, MovementStepOutcome.EnvironmentInvalid); }
    }
}
