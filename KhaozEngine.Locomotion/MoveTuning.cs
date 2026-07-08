using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 3, run 6, half-height 0.9 for a 1.8 m capsule, 45 deg max slope,
/// footprint radius 0.4 for static-world collision) plus the vertical-physics feel (gravity 25, jump 8,
/// terminal 50, 0.1 s coyote + buffer, full air control, 0.3 m grounded skin).
/// </summary>
public readonly record struct MoveTuning(
    float WalkSpeed,
    float RunSpeed,
    float CapsuleHalfHeight,
    float MaxSlopeRadians,
    float CapsuleRadius = 0.4f,
    float Gravity = 25f,
    float JumpSpeed = 8f,
    float MaxFallSpeed = 50f,
    float CoyoteTime = 0.1f,
    float JumpBuffer = 0.1f,
    float AirControl = 1f,
    float GroundedEpsilon = 0.3f,
    float StepHeight = 0.4f,
    float WadeStartDepthFraction = 0.15f,
    float WadeEndDepthFraction = 0.65f,
    float WadeMinSpeedScale = 0.45f)
{
    /// <summary>Walkable-slice defaults: walk 3 m/s, run 6 m/s, capsule half-height 0.9 m, max slope 45 deg
    /// (steep enough for normal hills, low enough that a RimFeature mountain wall is rejected, so the slope gate
    /// keeps the rim un-climbable when a <c>groundNormal</c> delegate is supplied), capsule footprint radius
    /// 0.4 m used by static-world collision, plus vertical physics: gravity 25 m/s^2, jump launch 8 m/s
    /// (apex ~1.28 m), terminal fall 50 m/s, 0.1 s coyote-time + jump-buffer, full (1.0) air control, and a
    /// 0.3 m grounded skin so a downhill run does not jitter between grounded and airborne.</summary>
    public static MoveTuning Default => new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: MathF.PI * 45f / 180f,
        CapsuleRadius: 0.4f);

    /// <summary>Gravity acceleration magnitude (m/s^2), applied downward each tick.</summary>
    public float Gravity { get; init; } = Gravity;

    /// <summary>Upward launch velocity (m/s) imparted by a jump.</summary>
    public float JumpSpeed { get; init; } = JumpSpeed;

    /// <summary>Terminal fall speed (m/s); vertical velocity is clamped to <c>-MaxFallSpeed</c>.</summary>
    public float MaxFallSpeed { get; init; } = MaxFallSpeed;

    /// <summary>Grace window (seconds) after leaving the ground during which a jump still fires (coyote-time).</summary>
    public float CoyoteTime { get; init; } = CoyoteTime;

    /// <summary>Window (seconds) within which a jump pressed before landing fires on contact (jump-buffer).</summary>
    public float JumpBuffer { get; init; } = JumpBuffer;

    /// <summary>Scale applied to horizontal movement while airborne (1 = full control, 0 = none).</summary>
    public float AirControl { get; init; } = AirControl;

    /// <summary>Grounded skin (metres): while already grounded, ground within this distance below the feet keeps
    /// the capsule grounded (snaps it down), so a downhill slope does not jitter grounded/airborne. Kept small so
    /// it is a slope-stick, distinct from the larger <see cref="StepHeight"/> mount.</summary>
    public float GroundedEpsilon { get; init; } = GroundedEpsilon;

    /// <summary>Max upward support rise (metres) auto-mounted while grounded without a jump (a low rock/curb/log);
    /// a larger rise behaves as a wall (the move is blocked). Used by the surface-aware vertical step.</summary>
    public float StepHeight { get; init; } = StepHeight;

    /// <summary>Wade ramp start: submersion depth (as a fraction of the character's full body height, 2 *
    /// <see cref="CapsuleHalfHeight"/>) at or below which wading has NO speed penalty. Default 0.15 (~ankle depth on a
    /// 1.8 m body). Only consulted when the medium provider reports the sample in water; a null provider or dry
    /// sample never touches the ramp.</summary>
    public float WadeStartDepthFraction { get; init; } = WadeStartDepthFraction;

    /// <summary>Wade ramp end: submersion depth (fraction of full body height) at or above which the wade speed sits
    /// at its <see cref="WadeMinSpeedScale"/> floor. Default 0.65 (~chest depth). Between start and end the scale
    /// lerps linearly from full speed down to the floor. Must be &gt; <see cref="WadeStartDepthFraction"/>.</summary>
    public float WadeEndDepthFraction { get; init; } = WadeEndDepthFraction;

    /// <summary>Wade speed floor: the horizontal-speed multiplier at (and past) chest depth,
    /// <see cref="WadeEndDepthFraction"/>. Default 0.45 (chest-deep wading is a bit under half speed). The medium's
    /// own <c>WadeSpeedScale</c> composes as a further multiplier on top of the depth ramp.</summary>
    public float WadeMinSpeedScale { get; init; } = WadeMinSpeedScale;
}
