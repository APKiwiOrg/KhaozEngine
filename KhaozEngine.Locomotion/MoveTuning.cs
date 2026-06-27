using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 3, run 6, half-height 0.9 for a 1.8 m capsule, ~50 deg max slope).
/// </summary>
public readonly record struct MoveTuning(
    float WalkSpeed,
    float RunSpeed,
    float CapsuleHalfHeight,
    float MaxSlopeRadians)
{
    /// <summary>Walkable-slice defaults: walk 3 m/s, run 6 m/s, capsule half-height 0.9 m, max slope ~50 deg.</summary>
    public static MoveTuning Default => new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: MathF.PI * 50f / 180f);
}
