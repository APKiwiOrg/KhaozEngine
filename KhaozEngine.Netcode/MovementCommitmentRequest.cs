using System;
using System.Numerics;

// Lives in KhaozEngine.Netcode under its shipped NetWorld name, because IAdminControllable names it. See
// IAdminControllable.cs.
namespace KhaozEngine.NetWorld;

/// <summary>An immutable request for a server-authored committed ballistic move.
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
        Direction = Vector2.Normalize(direction);
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

    // The refusals KhaozEngine.Locomotion.MovementCommitment's constructor makes, restated here because this type now
    // lives below Locomotion and cannot build one to borrow them. The same order, the same exception types, the same
    // parameter names and the same message, so a caller cannot tell which of the two refused it.
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
