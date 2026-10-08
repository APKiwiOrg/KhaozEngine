using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Explicit movement under a caller-owned read lease. The caller publishes the returned
/// pure state before releasing that lease. No physical mutation occurs in this kernel.</summary>
public static partial class ExplicitCharacterMovement
{
    public static MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        if (!float.IsFinite(command.Move.X) || !float.IsFinite(command.Move.Y) || !float.IsFinite(command.CameraYaw))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (direction, fraction, run) = CharacterMovement.ResolveCameraCommand(command, tuning);
        return StepCore(state, direction, fraction, run, command.Jump,
            command.FaceCamera ? command.CameraYaw : null, deltaSeconds, tuning, water, queries, boundary);
    }

    public static MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (resolved, fraction) = CharacterMovement.ResolveWorldDir(direction, preserveSmallMagnitude: true);
        return StepCore(state, resolved, fraction, run, false, null, deltaSeconds, tuning, water, queries, boundary);
    }

    static MovementStepResult StepCore(in FramedMovementState input, Vector2 direction, float fraction,
        bool run, bool jump, float? facing, float dt, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!input.IsValid || !Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy ||
            !float.IsFinite(dt) || dt <= 0 || !float.IsFinite(fraction) || !ValidCarriedState(input.State) ||
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
            bool wasSwimming = input.State.Swimming || input.State.WaterExcursion == WaterExcursionState.Surface;
            MovementBodyQuery body = Body(next.Position, tuning, selection);
            MovementStepOutcome placement = WaterPlacement(body, tuning, water, queries, boundary);
            if (placement != MovementStepOutcome.Advanced) return Hold(input, placement);
            status = Move(body, Vector3.Zero, queries, water, tuning, boundary, out _, out _);
            if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            MovementWaterPoint point = queries.SampleCentreWater(body);
            if (point.Availability != MovementAvailability.Known) return Hold(input, Outcome(point.Availability));
            if (point.Space != selection.Space) return Hold(input, MovementStepOutcome.EnvironmentInvalid);

            bool physicalSupport = false;
            if (next.Grounded || next.VerticalVelocity <= 0)
            {
                status = Settle(ref next, ref selection, tuning.GroundedEpsilon, tuning, water, queries, boundary, out physicalSupport);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
                point = queries.SampleCentreWater(Body(next.Position, tuning, selection));
                if (point.Availability != MovementAvailability.Known) return Hold(input, Outcome(point.Availability));
            }
            bool supported = physicalSupport && FootingAllowed(point, wasSwimming, next.Position.Y, tuning, water);
            next.Grounded = supported;
            if (physicalSupport && next.VerticalVelocity < 0) next.VerticalVelocity = 0;
            if (supported) { next.VerticalVelocity = 0; next.TimeSinceGrounded = 0; }
            else selection = new(selection.Space, null, selection.Identity);
            ClassifyWater(ref next, point, supported, wasSwimming, tuning, water, input.State.VerticalVelocity < 0);

            bool fluidMotion = next.Swimming || next.WaterExcursion == WaterExcursionState.AirborneFromWater;
            bool landInput = !fluidMotion && (!point.InWater || supported);
            bool jumpRequested = landInput && (jump || input.State.JumpBufferRemaining > 0);
            float sinceGround = supported ? 0 : input.State.TimeSinceGrounded + dt;
            next.JumpBufferRemaining = landInput
                ? jump ? tuning.JumpBuffer : Math.Max(0, input.State.JumpBufferRemaining - dt) : 0;
            bool launched;
            float velocity, targetY;
            if (fluidMotion)
            {
                status = SurfaceWaterMotion.TryPropose(next, !next.Swimming, jump, point, dt, tuning,
                    water.SurfaceJumpSpeed, out Vector2 vertical, out launched);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
                targetY = vertical.X;
                velocity = vertical.Y;
                if (launched) { next.Swimming = false; next.WaterExcursion = WaterExcursionState.AirborneFromWater; }
            }
            else
            {
                launched = jumpRequested && (supported || sinceGround <= tuning.CoyoteTime);
                if (launched) next.JumpBufferRemaining = 0;
                velocity = launched ? tuning.JumpSpeed : next.VerticalVelocity;
                if (!supported || launched)
                    velocity = (float)Math.Max(-tuning.MaxFallSpeed, velocity - (double)tuning.Gravity * dt);
                targetY = next.Position.Y + velocity * dt;
            }
            float speed = (fluidMotion ? tuning.SwimSpeed : run ? tuning.RunSpeed : tuning.WalkSpeed) *
                fraction * next.SpeedScale * MediumSpeed(point, next.Position.Y, fluidMotion, tuning);
            Vector2 horizontal = !fluidMotion && !supported
                ? tuning.AirMomentum
                    ? CharacterMovement.ResolveAirborneVelocity(input.State.HorizontalVelocity, direction, speed, dt, tuning)
                    : direction * (speed * tuning.AirControl)
                : direction * speed;
            if (!float.IsFinite(horizontal.X) || !float.IsFinite(horizontal.Y) || !float.IsFinite(velocity) || !float.IsFinite(targetY))
                return Hold(input, MovementStepOutcome.EnvironmentInvalid);
            Vector3 delta = new(horizontal.X * dt, targetY - next.Position.Y, horizontal.Y * dt);
            Vector3 origin = next.Position;
            status = Move(Body(origin, tuning, selection), delta, queries, water, tuning, boundary, out Vector3 position, out bool blocked);
            if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
            if (blocked && (supported || next.Swimming) && !launched)
            {
                status = TryStepUp(Body(origin, tuning, selection), new(delta.X, delta.Z), tuning,
                    water, queries, boundary, out MovementSupportPlacement? step);
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
            next.HorizontalVelocity = CharacterMovement.ClipToAchieved(horizontal,
                new(position.X - origin.X, position.Z - origin.Z), dt);
            next.FacingYaw = CharacterMovement.ResolveFacing(next.FacingYaw, direction, facing, dt, tuning);
            next.TimeSinceGrounded = launched ? tuning.CoyoteTime + dt : sinceGround;
            next.SupportGranted = supported;
            selection = new(selection.Space, null, selection.Identity);

            if (velocity <= 0 && !launched)
            {
                float drop = supported ? tuning.StepHeight + tuning.GroundedEpsilon : tuning.GroundedEpsilon;
                status = Settle(ref next, ref selection, drop, tuning, water, queries, boundary, out bool landed);
                if (status != MovementAvailability.Known) return Hold(input, Outcome(status));
                point = queries.SampleCentreWater(Body(next.Position, tuning, selection));
                if (point.Availability != MovementAvailability.Known) return Hold(input, Outcome(point.Availability));
                if (landed)
                {
                    next.VerticalVelocity = 0;
                    next.Grounded = FootingAllowed(point, wasSwimming, next.Position.Y, tuning, water);
                    if (next.Grounded)
                    {
                        next.TimeSinceGrounded = 0;
                        next.SupportGranted = true;
                        if (!input.State.Grounded) next.LandingImpactSpeed = Math.Max(0, -velocity);
                    }
                }
            }
            else if (blocked && position.Y < targetY - water.ContactSkinMetres)
                next.VerticalVelocity = 0;

            MovementWaterPoint destination = queries.SampleCentreWater(Body(next.Position, tuning, selection));
            if (destination.Availability != MovementAvailability.Known) return Hold(input, Outcome(destination.Availability));
            ClassifyWater(ref next, destination, next.Grounded, wasSwimming || next.Swimming, tuning, water, velocity < 0);
            if (next.Swimming || next.WaterExcursion == WaterExcursionState.AirborneFromWater ||
                destination.InWater && !next.Grounded)
                next.JumpBufferRemaining = 0;
            else if (!launched && jumpRequested && next.Grounded)
            {
                // The landing approach has already been proved. Latch the buffered launch without
                // inventing another position advance. Its upward segment is resolved next tick.
                next.VerticalVelocity = tuning.JumpSpeed;
                next.Grounded = false;
                next.TimeSinceGrounded = tuning.CoyoteTime + dt;
                next.JumpBufferRemaining = 0;
            }
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
