using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>The lifecycle of a server-authored movement commitment.</summary>
public enum MovementCommitmentPhase : byte
{
    None,
    Preparing,
    Airborne,
    Recovering,
    Completed,
    Aborted,
}

/// <summary>Why a movement commitment stopped.</summary>
public enum MovementCommitmentEndReason : byte
{
    None,
    Landed,
    Blocked,
    TimedOut,
    EnteredWater,
    Aborted,
    Teleported,
    Superseded,
    Disconnected,
}

/// <summary>
/// Carried state for one committed ballistic move. While active, player commands cannot alter its travel, jump, or
/// facing. The state is replicated so authoritative correction and pending-command replay continue the same move.
/// </summary>
public readonly record struct MovementCommitment
{
    public MovementCommitment(uint sequence, Vector2 direction, float horizontalSpeed, float verticalSpeed,
        float preparationSeconds, float timeoutSeconds)
        : this(sequence, direction, horizontalSpeed, verticalSpeed, gravity: 25f, preparationSeconds,
            recoverySeconds: 0f, timeoutSeconds)
    {
    }

    public MovementCommitment(uint sequence, Vector2 direction, float horizontalSpeed, float verticalSpeed,
        float gravity, float preparationSeconds, float recoverySeconds, float timeoutSeconds)
    {
        if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() <= 1e-8f)
            throw new ArgumentException("Direction must be finite and nonzero.", nameof(direction));
        if (!float.IsFinite(horizontalSpeed) || horizontalSpeed < 0f)
            throw new ArgumentOutOfRangeException(nameof(horizontalSpeed));
        if (!float.IsFinite(verticalSpeed) || verticalSpeed <= 0f)
            throw new ArgumentOutOfRangeException(nameof(verticalSpeed));
        if (!float.IsFinite(gravity) || gravity <= 0f) throw new ArgumentOutOfRangeException(nameof(gravity));
        if (!float.IsFinite(preparationSeconds) || preparationSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(preparationSeconds));
        if (!float.IsFinite(recoverySeconds) || recoverySeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(recoverySeconds));
        if (!float.IsFinite(timeoutSeconds) || timeoutSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        Sequence = sequence;
        Phase = MovementCommitmentPhase.Preparing;
        Direction = Vector2.Normalize(direction);
        HorizontalSpeed = horizontalSpeed;
        VerticalSpeed = verticalSpeed;
        Gravity = gravity;
        PreparationRemaining = preparationSeconds;
        RecoveryRemaining = recoverySeconds;
        TimeoutRemaining = timeoutSeconds;
        EndReason = MovementCommitmentEndReason.None;
    }

    public uint Sequence { get; init; }
    public MovementCommitmentPhase Phase { get; init; }
    public Vector2 Direction { get; init; }
    public float HorizontalSpeed { get; init; }
    public float VerticalSpeed { get; init; }
    public float Gravity { get; init; }
    public float PreparationRemaining { get; init; }
    public float RecoveryRemaining { get; init; }
    public float TimeoutRemaining { get; init; }
    public MovementCommitmentEndReason EndReason { get; init; }

    public bool IsActive => Phase is MovementCommitmentPhase.Preparing or MovementCommitmentPhase.Airborne
        or MovementCommitmentPhase.Recovering;
}
