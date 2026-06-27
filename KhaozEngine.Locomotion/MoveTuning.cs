using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 3, run 6, half-height 0.9 for a 1.8 m capsule, 45 deg max slope).
/// </summary>
public readonly record struct MoveTuning(
    float WalkSpeed,
    float RunSpeed,
    float CapsuleHalfHeight,
    float MaxSlopeRadians)
{
    /// <summary>Walkable-slice defaults: walk 3 m/s, run 6 m/s, capsule half-height 0.9 m, max slope 45 deg
    /// (steep enough for normal hills, low enough that a RimFeature mountain wall is rejected, so the slope gate
    /// keeps the rim un-climbable when a <c>groundNormal</c> delegate is supplied).</summary>
    public static MoveTuning Default => new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: MathF.PI * 45f / 180f);
}
