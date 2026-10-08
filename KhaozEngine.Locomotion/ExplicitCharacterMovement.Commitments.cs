using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    static bool PrepareCommitment(ref MoveState state, ref MoveTuning tuning, ref Vector2 direction,
        ref float fraction, ref bool run, ref bool jump, ref float? facing, float dt,
        out bool flight, out bool launch)
    {
        if (state.Commitment.IsActive &&
            (state.Swimming || state.WaterExcursion == WaterExcursionState.AirborneFromWater))
        {
            state.Commitment = WaterAborted(state.Commitment);
            direction = Vector2.Zero;
            fraction = 0;
            run = jump = false;
            facing = null;
            flight = launch = false;
            return true;
        }
        return CharacterMovement.PrepareCommitmentTick(ref state, ref tuning, ref direction, ref fraction,
            ref run, ref jump, ref facing, dt, out flight, out launch);
    }

    static MovementCommitment WaterAborted(in MovementCommitment movement) => movement with
    {
        Phase = MovementCommitmentPhase.Aborted,
        EndReason = MovementCommitmentEndReason.EnteredWater
    };

    static bool ValidCommitment(in MovementCommitment movement)
    {
        if (movement.Phase is < MovementCommitmentPhase.None or > MovementCommitmentPhase.Aborted ||
            movement.EndReason is < MovementCommitmentEndReason.None or > MovementCommitmentEndReason.Disconnected)
            return false;
        if (!movement.IsActive) return true;
        return movement.Sequence != 0 && float.IsFinite(movement.Direction.X) && float.IsFinite(movement.Direction.Y) &&
            MathF.Abs(movement.Direction.LengthSquared() - 1) <= 0.00001f &&
            Nonnegative(movement.HorizontalSpeed) && Positive(movement.VerticalSpeed) && Positive(movement.Gravity) &&
            Nonnegative(movement.PreparationRemaining) && Nonnegative(movement.RecoveryRemaining) &&
            Nonnegative(movement.TimeoutRemaining);
    }
}
