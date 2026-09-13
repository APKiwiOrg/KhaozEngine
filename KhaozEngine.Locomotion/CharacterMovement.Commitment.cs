using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    private static bool PrepareCommitmentTick(ref MoveState state, ref MoveTuning tuning, ref Vector2 moveDir,
        ref float speedFraction, ref bool run, ref bool jump, ref float? faceYaw, float dt,
        out bool committedFlight, out bool launchedThisTick)
    {
        MovementCommitment movement = state.Commitment;
        committedFlight = false;
        launchedThisTick = false;

        if (movement.Phase is MovementCommitmentPhase.Completed or MovementCommitmentPhase.Aborted)
        {
            state.Commitment = default;
            return false;
        }
        if (!movement.IsActive) return false;

        moveDir = Vector2.Zero;
        speedFraction = 0f;
        run = false;
        jump = false;
        faceYaw = null;

        float timeout = MathF.Max(0f, movement.TimeoutRemaining - dt);
        movement = movement with { TimeoutRemaining = timeout };
        if (timeout <= 0f)
        {
            state.HorizontalVelocity = Vector2.Zero;
            state.Commitment = movement with
            {
                Phase = MovementCommitmentPhase.Aborted,
                EndReason = MovementCommitmentEndReason.TimedOut,
            };
            return true;
        }

        if (movement.Phase == MovementCommitmentPhase.Recovering)
        {
            float recovery = MathF.Max(0f, movement.RecoveryRemaining - dt);
            state.Commitment = movement with
            {
                RecoveryRemaining = recovery,
                Phase = recovery <= 0f ? MovementCommitmentPhase.Completed : MovementCommitmentPhase.Recovering,
                EndReason = recovery <= 0f ? MovementCommitmentEndReason.Landed : MovementCommitmentEndReason.None,
            };
            state.HorizontalVelocity = Vector2.Zero;
            return true;
        }

        if (movement.Phase == MovementCommitmentPhase.Preparing)
        {
            float preparation = MathF.Max(0f, movement.PreparationRemaining - dt);
            movement = movement with { PreparationRemaining = preparation };
            if (preparation > 1e-6f)
            {
                state.Commitment = movement;
                state.HorizontalVelocity = Vector2.Zero;
                return true;
            }

            movement = movement with { Phase = MovementCommitmentPhase.Airborne };
            // The shared vertical integrator is semi-implicit: it applies gravity before this tick's position
            // advance. Seed at the half-step so tick-boundary positions follow the authored ballistic equation while
            // VerticalSpeed remains the nominal launch speed consumers use for progress.
            state.VerticalVelocity = movement.VerticalSpeed + movement.Gravity * dt * 0.5f;
            state.Grounded = false;
            state.TimeSinceGrounded = 0f;
            state.HorizontalVelocity = movement.Direction * movement.HorizontalSpeed;
            launchedThisTick = true;
        }

        state.Commitment = movement;
        tuning = tuning with { Gravity = movement.Gravity };
        committedFlight = movement.Phase == MovementCommitmentPhase.Airborne;
        return true;
    }

    private static MovementCommitment FinishCommitmentTick(in MovementCommitment movement, bool launchedThisTick,
        bool grounded, in Vector3 start, in Vector3 end, float dt)
    {
        if (movement.Phase != MovementCommitmentPhase.Airborne) return movement;

        if (launchedThisTick && movement.HorizontalSpeed > 0f)
        {
            float intended = movement.HorizontalSpeed * dt;
            float achieved = new Vector2(end.X - start.X, end.Z - start.Z).Length();
            if (achieved < intended * 0.1f)
                return movement with
                {
                    Phase = MovementCommitmentPhase.Aborted,
                    EndReason = MovementCommitmentEndReason.Blocked,
                };
        }

        if (!grounded) return movement;
        return movement.RecoveryRemaining > 0f
            ? movement with { Phase = MovementCommitmentPhase.Recovering }
            : movement with
            {
                Phase = MovementCommitmentPhase.Completed,
                EndReason = MovementCommitmentEndReason.Landed,
            };
    }
}
