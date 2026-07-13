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

    /// <summary>Signed step-climb rate in m/s: the vertical speed at which the capsule is riding a paced STEP climb this
    /// tick. Positive = ascending a continuous paced stair run (the step-up co-paces the rise to
    /// <see cref="MoveTuning.MaxStepClimbSpeed"/>); negative = descending a stepped-down riser (the step-down
    /// grounded-hold seats the capsule one riser down while staying grounded); exactly 0 = not on a step climb (flat
    /// ground, a terrain slope, a jump, a fall, a swim, or a single discrete riser seat that is not part of a run). It
    /// is a state OUTPUT of the step, carried like <see cref="VerticalVelocity"/>, and is the SINGLE source of truth a
    /// presentation smoother reads to glide the drawn feet up/down the stair slope: 0 means "not climbing" (render raw,
    /// by construction), a signed rate means "glide at exactly this rate" (no position-delta estimation). <c>default</c>
    /// is 0 (a non-climber, byte-identical to a pre-feature state). Replicated quantized as <c>MovementState.ClimbRateQ</c>
    /// in NetWorld so remotes glide on the same signal the local owner does.</summary>
    public float ClimbRate;
}
