using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>An immutable request for a server-authored committed ballistic move.</summary>
public readonly struct MovementCommitmentRequest
{
    public MovementCommitmentRequest(Vector2 direction, float horizontalSpeed, float verticalSpeed,
        float preparationSeconds = 0f, float recoverySeconds = 0f, float timeoutSeconds = 5f, float gravity = 25f)
    {
        MovementCommitment validated = new(1u, direction, horizontalSpeed, verticalSpeed, gravity,
            preparationSeconds, recoverySeconds, timeoutSeconds);
        Direction = validated.Direction;
        HorizontalSpeed = horizontalSpeed;
        VerticalSpeed = verticalSpeed;
        Gravity = gravity;
        PreparationSeconds = preparationSeconds;
        RecoverySeconds = recoverySeconds;
        TimeoutSeconds = timeoutSeconds;
    }

    public Vector2 Direction { get; }
    public float HorizontalSpeed { get; }
    public float VerticalSpeed { get; }
    public float Gravity { get; }
    public float PreparationSeconds { get; }
    public float RecoverySeconds { get; }
    public float TimeoutSeconds { get; }

    /// <summary>Builds a same-height ballistic arc from authored distance, apex height, and duration.</summary>
    public static MovementCommitmentRequest ForBallisticArc(Vector2 direction, float distance, float apexHeight,
        float durationSeconds, float preparationSeconds = 0f, float recoverySeconds = 0f,
        float timeoutSeconds = 5f)
    {
        if (!float.IsFinite(distance) || distance < 0f) throw new ArgumentOutOfRangeException(nameof(distance));
        if (!float.IsFinite(apexHeight) || apexHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(apexHeight));
        if (!float.IsFinite(durationSeconds) || durationSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        float horizontalSpeed = distance / durationSeconds;
        float verticalSpeed = 4f * apexHeight / durationSeconds;
        float gravity = 8f * apexHeight / (durationSeconds * durationSeconds);
        return new MovementCommitmentRequest(direction, horizontalSpeed, verticalSpeed, preparationSeconds,
            recoverySeconds, timeoutSeconds, gravity);
    }

    internal MovementCommitment Start(uint sequence) => new(sequence, Direction, HorizontalSpeed, VerticalSpeed,
        Gravity, PreparationSeconds, RecoverySeconds, TimeoutSeconds);

    internal void Validate() => _ = new MovementCommitment(1u, Direction, HorizontalSpeed, VerticalSpeed, Gravity,
        PreparationSeconds, RecoverySeconds, TimeoutSeconds);
}

/// <summary>The authoritative result of one commitment lifecycle transition.</summary>
public readonly record struct MovementCommitmentResult(int Slot, uint Sequence, MovementCommitmentEndReason Reason,
    Vector3 Position, Vector2 Direction);
