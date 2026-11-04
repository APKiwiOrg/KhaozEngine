using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Ground commitment creation and sequence-checked authoritative controls. Travel spends a distance
/// budget, then holds for steering or an explicit arrival. It never infers arrival from an old endpoint.</summary>
public static class GroundTravelCommitment
{
    internal static MovementCommitment Create(uint sequence, Vector2 direction, float speed, float distance,
        float preparationSeconds, float recoverySeconds, float timeoutSeconds)
    {
        RequireSequence(sequence);
        RequireDirection(direction);
        if (!float.IsFinite(speed) || speed <= 0f) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!float.IsFinite(distance) || distance <= 0f) throw new ArgumentOutOfRangeException(nameof(distance));
        if (!float.IsFinite(preparationSeconds) || preparationSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(preparationSeconds));
        if (!float.IsFinite(recoverySeconds) || recoverySeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(recoverySeconds));
        if (!float.IsFinite(timeoutSeconds) || timeoutSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        return new()
        {
            Sequence = sequence,
            Kind = MovementCommitmentKind.Ground,
            Phase = MovementCommitmentPhase.Preparing,
            Direction = MovementCommitment.NormalizeDirection(direction, ground: true),
            HorizontalSpeed = speed,
            DistanceRemaining = distance,
            PreparationRemaining = preparationSeconds,
            RecoveryRemaining = recoverySeconds,
            TimeoutRemaining = timeoutSeconds,
        };
    }

    /// <summary>Re-aims preparing or travelling ground state. Zero distance holds. Stalls and timers are retained.
    /// Invalid arguments throw even if the sequence is stale.</summary>
    public static MovementCommitment Steer(in MovementCommitment current, uint sequence, Vector2 direction,
        float distanceRemaining)
    {
        RequireSequence(sequence);
        RequireDirection(direction);
        if (!float.IsFinite(distanceRemaining) || distanceRemaining < 0f)
            throw new ArgumentOutOfRangeException(nameof(distanceRemaining));
        return Controllable(current, sequence)
            ? current with { Direction = MovementCommitment.NormalizeDirection(direction, ground: true), DistanceRemaining = distanceRemaining }
            : current;
    }

    /// <summary>Ends preparing or travelling ground state. Arrived optionally recovers. Other terminal reasons
    /// abort. Recovery and stale sequences are unchanged, so repeated arrival cannot restart recovery.</summary>
    public static MovementCommitment End(in MovementCommitment current, uint sequence, MovementCommitmentEndReason reason)
    {
        RequireSequence(sequence);
        if (reason is <= MovementCommitmentEndReason.Landed or > MovementCommitmentEndReason.UnsupportedState)
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (!Controllable(current, sequence)) return current;
        return current with
        {
            DistanceRemaining = 0,
            EndReason = reason,
            Phase = reason == MovementCommitmentEndReason.Arrived
                ? current.RecoveryRemaining > 0 ? MovementCommitmentPhase.Recovering : MovementCommitmentPhase.Completed
                : MovementCommitmentPhase.Aborted,
        };
    }

    /// <summary>Accepted ground arrival remains the terminal outcome when recovery releases for any reason.
    /// Before arrival, and for ballistic moves, the supplied release reason is retained.</summary>
    public static MovementCommitmentEndReason ResolveEndReason(in MovementCommitment movement,
        MovementCommitmentEndReason reason) =>
        movement.Kind == MovementCommitmentKind.Ground && movement.EndReason == MovementCommitmentEndReason.Arrived
            ? MovementCommitmentEndReason.Arrived : reason;

    internal static void PrepareTick(ref MoveState state, ref MoveTuning tuning, ref Vector2 direction,
        ref float fraction, float dt)
    {
        MovementCommitment movement = state.Commitment;
        state.JumpBufferRemaining = 0;
        state.HorizontalVelocity = Vector2.Zero;
        // Arrival finishes travel. Recovery is bounded by its own remaining time, including when travel
        // arrived on its final eligible tick. Ballistic timeout policy stays in the shared flight path.
        if (movement.EndReason != MovementCommitmentEndReason.Arrived)
        {
            movement = movement with { TimeoutRemaining = MathF.Max(0, movement.TimeoutRemaining - dt) };
            if (movement.TimeoutRemaining <= 0)
            {
                state.Commitment = Abort(movement, MovementCommitmentEndReason.TimedOut);
                return;
            }
        }
        state.FacingYaw = CharacterMovement.FacingYawOf(movement.Direction);
        if (state.Swimming || state.WaterExcursion == WaterExcursionState.AirborneFromWater)
            movement = Abort(movement, MovementCommitmentEndReason.EnteredWater);
        else if (!state.Grounded)
            movement = Abort(movement, MovementCommitmentEndReason.UnsupportedState);
        else if (movement.Phase == MovementCommitmentPhase.Recovering)
        {
            float recovery = MathF.Max(0, movement.RecoveryRemaining - dt);
            movement = movement with
            {
                RecoveryRemaining = recovery,
                Phase = recovery <= 0 ? MovementCommitmentPhase.Completed : MovementCommitmentPhase.Recovering,
            };
        }
        else
        {
            if (movement.Phase == MovementCommitmentPhase.Preparing)
            {
                float preparation = MathF.Max(0, movement.PreparationRemaining - dt);
                movement = movement with
                {
                    PreparationRemaining = preparation,
                    Phase = preparation <= 1e-6f ? MovementCommitmentPhase.Travelling : MovementCommitmentPhase.Preparing,
                };
            }
            if (movement.Phase == MovementCommitmentPhase.Travelling)
            {
                if (!ResolvesPosition(state.Position, movement.Direction, movement.DistanceRemaining))
                    movement = movement with { DistanceRemaining = 0 };
                // Only the local horizontal drive changes. The controller retains its gravity, traction,
                // step budget, support and swept collision. SpeedScale is bypassed at the two drive sites.
                tuning = tuning with { WalkSpeed = (float)(IntendedDistance(movement, dt) / dt) };
                direction = movement.Direction;
                fraction = movement.DistanceRemaining > 0 ? 1 : 0;
            }
        }
        state.Commitment = movement;
    }

    internal static MovementCommitment FinishTick(in MovementCommitment movement, bool grounded,
        in Vector3 start, in Vector3 end, float dt, float controllerResolution)
    {
        if (!movement.IsActive) return movement;
        if (!grounded) return Abort(movement, MovementCommitmentEndReason.UnsupportedState);
        if (movement.Phase != MovementCommitmentPhase.Travelling) return movement;
        double intended = IntendedDistance(movement, dt);
        if (!(intended > 0)) return movement;
        // Match ballistic progress: achieved XZ displacement length, including collide-and-slide.
        float achieved = new Vector2(end.X - start.X, end.Z - start.Z).Length();
        float remaining = MathF.Max(0, movement.DistanceRemaining - achieved);
        if (remaining <= controllerResolution || !ResolvesPosition(end, movement.Direction, remaining)) remaining = 0;
        bool resolvable = intended > controllerResolution && ResolvesPosition(start, movement.Direction, (float)intended);
        byte blocked = achieved < intended * 0.1 && resolvable
            ? (byte)Math.Min(3, movement.ConsecutiveBlockedTicks + 1) : (byte)0;
        MovementCommitment result = movement with { DistanceRemaining = remaining, ConsecutiveBlockedTicks = blocked };
        return blocked >= 3 ? Abort(result, MovementCommitmentEndReason.Blocked) : result;
    }

    internal static void StopTerminalMotion(ref MoveState state)
    {
        if (state.Commitment.Kind == MovementCommitmentKind.Ground &&
            (!state.Commitment.IsActive || state.Commitment.DistanceRemaining <= 0))
            state.HorizontalVelocity = Vector2.Zero;
    }

    static MovementCommitment Abort(in MovementCommitment movement, MovementCommitmentEndReason reason) =>
        movement with { Phase = MovementCommitmentPhase.Aborted, EndReason = ResolveEndReason(movement, reason), DistanceRemaining = 0 };

    static double IntendedDistance(in MovementCommitment movement, float dt) =>
        Math.Min(movement.DistanceRemaining, (double)movement.HorizontalSpeed * dt);

    // Both controllers add a binary32 horizontal delta to the carried position. Use that same rounding
    // operation to decide whether a residual can move either axis. This handles directional spacing and
    // round-to-even ties without swallowing precise travel because an unrelated axis has coarse spacing.
    // Legacy additionally drops sweep remainders at its own named limit, supplied by that controller.
    static bool ResolvesPosition(in Vector3 position, in Vector2 direction, float distance)
    {
        Vector2 delta = direction * distance;
        return position.X + delta.X != position.X || position.Z + delta.Y != position.Z;
    }

    static bool Controllable(in MovementCommitment current, uint sequence) =>
        current.Kind == MovementCommitmentKind.Ground && current.Sequence == sequence &&
        current.Phase is MovementCommitmentPhase.Preparing or MovementCommitmentPhase.Travelling;

    static void RequireSequence(uint sequence)
    {
        if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
    }

    static void RequireDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction == Vector2.Zero)
            throw new ArgumentException("Direction must be finite and nonzero.", nameof(direction));
    }
}
