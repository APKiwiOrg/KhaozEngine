using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// The full kinematic state carried tick-to-tick by the vertical-aware
/// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, System.Func{float, float, float}, in MoveTuning, System.Func{float, float, Vector3}?, KhaozEngine.Physics.IPhysicsWorld?, System.Func{float, float, Vector2}?, System.Func{float, float, float, MovementMedium}?)"/>:
/// the capsule-centre <see cref="Position"/> plus the vertical axis. <see cref="VerticalVelocity"/> and
/// <see cref="Grounded"/> are the predicted/replicated state; <see cref="TimeSinceGrounded"/> (coyote-time
/// accounting) and <see cref="JumpBufferRemaining"/> (jump-buffer countdown) are the feel timers the step
/// evolves. The same state is run by the local controller, the authoritative server sim, and client prediction,
/// so it must round-trip exactly (it is replicated as <c>MovementState</c> in NetWorld). <c>default</c> is a
/// grounded-at-origin, zero-velocity state with no buffered jump.
/// </summary>
public struct MoveState
{
    /// <summary>Capsule-centre world position (Y = ground + half-height while grounded, free while airborne).</summary>
    public Vector3 Position;

    /// <summary>Vertical velocity in m/s (positive up). Zero while grounded; negative while falling.</summary>
    public float VerticalVelocity;

    /// <summary>True when the capsule is resting on the ground this tick; false while airborne.</summary>
    public bool Grounded;

    /// <summary>Seconds since the capsule was last grounded (drives coyote-time). Zero while grounded.</summary>
    public float TimeSinceGrounded;

    /// <summary>Seconds of jump-buffer remaining: set to <see cref="MoveTuning.JumpBuffer"/> on a jump press,
    /// counted down otherwise, consumed to zero when a jump fires. Zero (default) means no buffered jump, so a
    /// default state never spuriously jumps.</summary>
    public float JumpBufferRemaining;

    /// <summary>True while the capsule is SURFACE-SWIMMING: submersion crossed the <see cref="MoveTuning.SwimEnterDepthFraction"/>
    /// enter threshold (and has not yet fallen back below the lower <see cref="MoveTuning.SwimExitDepthFraction"/>
    /// exit threshold - the state carries tick-to-tick so the hysteresis band works). While set, gravity and
    /// ground-snap are suspended, the capsule settles to its buoyancy waterline, horizontal moves at
    /// <see cref="MoveTuning.SwimSpeed"/>, and a jump is a hop-out only in near-shore shallows. <c>default</c> (false)
    /// is a land character, so a pre-swim state is byte-identical. Replicated as <c>MovementState.Swimming</c> in
    /// NetWorld so the local owner reconciles it and remotes animate it.</summary>
    public bool Swimming;
}
