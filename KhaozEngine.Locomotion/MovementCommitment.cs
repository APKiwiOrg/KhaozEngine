using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>The physical travel policy of a movement commitment. Ballistic preserves the original default.</summary>
public enum MovementCommitmentKind : byte
{
    Ballistic = 0,
    Ground = 1,
}

/// <summary>The lifecycle of a server-authored movement commitment.</summary>
public enum MovementCommitmentPhase : byte
{
    None,
    Preparing,
    Airborne,
    Recovering,
    Completed,
    Aborted,
    Travelling,
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
    Arrived,
    TargetLost,
    NoPath,
    TooLong,
    UnsupportedState,
}

/// <summary>
/// Carried state for one committed ballistic or ground move. While active, player commands cannot alter its
/// travel, jump or facing. Authoritative correction and pending-command replay use the same carried state.
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
        Direction = NormalizeDirection(direction);
        HorizontalSpeed = horizontalSpeed;
        VerticalSpeed = verticalSpeed;
        Gravity = gravity;
        PreparationRemaining = preparationSeconds;
        RecoveryRemaining = recoverySeconds;
        TimeoutRemaining = timeoutSeconds;
        EndReason = MovementCommitmentEndReason.None;
    }

    /// <summary>Creates ground travel with a positive distance budget. Actor admission belongs to the caller.</summary>
    public static MovementCommitment ForGroundTravel(uint sequence, Vector2 direction, float speed, float distance,
        float preparationSeconds = 0f, float recoverySeconds = 0f, float timeoutSeconds = 5f) =>
        GroundTravelCommitment.Create(sequence, direction, speed, distance, preparationSeconds, recoverySeconds,
            timeoutSeconds);

    // Preserve the released arithmetic for ordinary ballistic inputs, while avoiding overflow for huge ones.
    internal static Vector2 NormalizeDirection(Vector2 direction, bool ground = false)
    {
        float lengthSquared = direction.LengthSquared();
        if (!ground && float.IsFinite(lengthSquared) && lengthSquared > 0f) return Vector2.Normalize(direction);
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
        return new((float)(direction.X / length), (float)(direction.Y / length));
    }

    public MovementCommitmentKind Kind { get; init; }
    public float DistanceRemaining { get; init; }
    public byte ConsecutiveBlockedTicks { get; init; }
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
        or MovementCommitmentPhase.Recovering or MovementCommitmentPhase.Travelling;
}
