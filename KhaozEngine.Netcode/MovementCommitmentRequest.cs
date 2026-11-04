using System;
using System.Numerics;

// Lives in KhaozEngine.Netcode under its shipped NetWorld name, because IAdminControllable names it. See
// IAdminControllable.cs.
namespace KhaozEngine.NetWorld;

/// <summary>An immutable request for a server-authored committed ballistic or ground move.
/// <para>Defined in the <c>KhaozEngine.Netcode</c> assembly under this namespace, and type-forwarded from
/// <c>KhaozEngine.NetWorld</c>, where it shipped. The float heads turn it into a
/// <c>KhaozEngine.Locomotion.MovementCommitment</c> when it starts.</para></summary>
public readonly struct MovementCommitmentRequest
{
    public MovementCommitmentRequest(Vector2 direction, float horizontalSpeed, float verticalSpeed,
        float preparationSeconds = 0f, float recoverySeconds = 0f, float timeoutSeconds = 5f, float gravity = 25f)
    {
        Require(direction, horizontalSpeed, verticalSpeed, gravity, preparationSeconds, recoverySeconds,
            timeoutSeconds);
        Direction = NormalizeDirection(direction);
        HorizontalSpeed = horizontalSpeed;
        VerticalSpeed = verticalSpeed;
        Gravity = gravity;
        PreparationSeconds = preparationSeconds;
        RecoverySeconds = recoverySeconds;
        TimeoutSeconds = timeoutSeconds;
    }

    private MovementCommitmentRequest(Vector2 direction, float speed, float distance, float preparationSeconds,
        float recoverySeconds, float timeoutSeconds)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction == Vector2.Zero)
            throw new ArgumentException("Direction must be finite and nonzero.", nameof(direction));
        if (!float.IsFinite(speed) || speed <= 0f) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!float.IsFinite(distance) || distance <= 0f) throw new ArgumentOutOfRangeException(nameof(distance));
        if (!float.IsFinite(preparationSeconds) || preparationSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(preparationSeconds));
        if (!float.IsFinite(recoverySeconds) || recoverySeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(recoverySeconds));
        if (!float.IsFinite(timeoutSeconds) || timeoutSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        Direction = NormalizeDirection(direction, ground: true);
        HorizontalSpeed = speed;
        DistanceRemaining = distance;
        PreparationSeconds = preparationSeconds;
        RecoverySeconds = recoverySeconds;
        TimeoutSeconds = timeoutSeconds;
        IsGroundTravel = true;
    }

    /// <summary>Creates positive-budget ground travel. The caller validates actor support and water state.</summary>
    public static MovementCommitmentRequest ForGroundTravel(Vector2 direction, float speed, float distance,
        float preparationSeconds = 0f, float recoverySeconds = 0f, float timeoutSeconds = 5f) =>
        new(direction, speed, distance, preparationSeconds, recoverySeconds, timeoutSeconds);

    private static Vector2 NormalizeDirection(Vector2 direction, bool ground = false)
    {
        float lengthSquared = direction.LengthSquared();
        if (!ground && float.IsFinite(lengthSquared) && lengthSquared > 0f) return Vector2.Normalize(direction);
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
        return new((float)(direction.X / length), (float)(direction.Y / length));
    }

    public bool IsGroundTravel { get; }
    public float DistanceRemaining { get; }
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

    // The refusals KhaozEngine.Locomotion.MovementCommitment's constructor makes, restated here because this type now
    // lives below Locomotion and cannot build one to borrow them. The same order, the same exception types, the same
    // parameter names, so a caller cannot tell which of the two refused it.
    // MovementCommitmentRequestParityTests in KhaozEngine.Server.Tests, the one project that sees both assemblies,
    // holds the two equal. The float heads still validate through MovementCommitment itself when a request starts, so
    // a drift here would move where a bad request is refused, never whether.
    private static void Require(Vector2 direction, float horizontalSpeed, float verticalSpeed, float gravity,
        float preparationSeconds, float recoverySeconds, float timeoutSeconds)
    {
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
    }
}
