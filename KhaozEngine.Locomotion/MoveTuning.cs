using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 6, run 12, half-height 0.9 for a 1.8 m capsule, 45 deg max slope,
/// footprint radius 0.4 for static-world collision) plus the vertical-physics feel (gravity 25, jump 9.79796,
/// terminal 50, 0.1 s coyote + buffer, full air control, 0.3 m grounded skin).
/// </summary>
public readonly record struct MoveTuning(
    float WalkSpeed,
    float RunSpeed,
    float CapsuleHalfHeight,
    float MaxSlopeRadians,
    float CapsuleRadius = 0.4f,
    float Gravity = 25f,
    float JumpSpeed = 9.79796f, // = 8 * sqrt(1.5), +50% apex vs the old 8f, matches Ruinborne's deliberate value
    float MaxFallSpeed = 50f,
    float CoyoteTime = 0.1f,
    float JumpBuffer = 0.1f,
    float AirControl = 1f,
    float GroundedEpsilon = 0.3f,
    float StepHeight = 0.4f,
    float WadeStartDepthFraction = 0.15f,
    float WadeEndDepthFraction = 0.65f,
    float WadeMinSpeedScale = 0.45f,
    float SwimEnterDepthFraction = 0.65f,
    float SwimExitDepthFraction = 0.55f,
    float SwimSpeed = 2.5f,
    float SwimSurfaceSubmersionFraction = 0.6f,
    float SwimBuoyancyStiffness = 8f,
    float MaxStepClimbSpeed = 3.5f)
{
    /// <summary>Walkable-slice defaults: walk 6 m/s, run 12 m/s, capsule half-height 0.9 m, max slope 45 deg
    /// (steep enough for normal hills, low enough that a RimFeature mountain wall is rejected, so the slope gate
    /// keeps the rim un-climbable when a <c>groundNormal</c> delegate is supplied), capsule footprint radius
    /// 0.4 m used by static-world collision, plus vertical physics: gravity 25 m/s^2, jump launch 9.79796 m/s
    /// (= 8 * sqrt(1.5), +50% apex vs the old 8f: apex ~1.92 m, matching Ruinborne's deliberate jump-height
    /// value), terminal fall 50 m/s, 0.1 s coyote-time + jump-buffer, full (1.0) air control, and a 0.3 m
    /// grounded skin so a downhill run does not jitter between grounded and airborne. This matches
    /// CharacterController3D's own field defaults exactly (same literal + comment in both places), so a caller
    /// building either way gets identical feel.</summary>
    public static MoveTuning Default => new(
        WalkSpeed: 6f,
        RunSpeed: 12f,
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

    /// <summary>Maximum vertical climb speed (metres/second) a step-up mount rises at. A detected step-up
    /// (curb, stair riser) no longer snaps the whole riser in a single tick: it rises toward the ledge by at most
    /// <c>MaxStepClimbSpeed * dt</c> per tick, holding horizontal progress against the riser until the feet reach
    /// the tread, so a stair run ascends at a steady walking pace instead of shooting up. Default 3.5: a single
    /// low curb (rise below one tick's budget) still mounts in one tick as before, while a tall run of risers is
    /// smoothed. A value &lt;= 0 disables the limit (instant snap, the pre-smoothing behaviour). Does not change
    /// horizontal walk speed and never makes a climbable step unclimbable.</summary>
    public float MaxStepClimbSpeed { get; init; } = MaxStepClimbSpeed;

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

    /// <summary>Swim ENTER threshold: the submersion depth (fraction of full body height, <c>2 *
    /// <see cref="CapsuleHalfHeight"/></c>) a walking/wading character must reach for the movement step to flip it
    /// into <see cref="MoveState.Swimming"/>. Default 0.65 (chest), exactly where the wade ramp bottoms out - swim
    /// begins where wading ends. Paired with the LOWER <see cref="SwimExitDepthFraction"/> so the enter/exit
    /// boundary has hysteresis and does not flicker on a gentle slope. Only consulted while the medium reports the
    /// sample <c>InWater</c>.</summary>
    public float SwimEnterDepthFraction { get; init; } = SwimEnterDepthFraction;

    /// <summary>Swim EXIT threshold: the submersion depth (fraction of full body height) below which a SWIMMING
    /// character drops back to walking/wading (or if the sample leaves the water entirely). Default 0.55, strictly
    /// below <see cref="SwimEnterDepthFraction"/> so there is a hysteresis band: a character standing right at chest
    /// depth cannot flicker between swim and wade across a tick. Also defines "near-shore shallows" for the swim
    /// hop-out jump: a jump pressed while swimming is honoured only once the feet are shallow enough to be within
    /// this exit band of leaving the water (see <see cref="CharacterMovement"/>). Must be &lt;
    /// <see cref="SwimEnterDepthFraction"/>.</summary>
    public float SwimExitDepthFraction { get; init; } = SwimExitDepthFraction;

    /// <summary>Horizontal swim speed (m/s) while <see cref="MoveState.Swimming"/>, replacing the walk/run speed.
    /// Default 2.5 (a touch under walk pace). The medium's <c>WadeSpeedScale</c> still composes on top (a zone can
    /// drag a swamp swim), and run has no effect while swimming.</summary>
    public float SwimSpeed { get; init; } = SwimSpeed;

    /// <summary>Buoyancy target: the fraction of the body that stays SUBMERGED once floating at rest, i.e. the
    /// submersion depth (fraction of body height) the settle converges the character to. Default 0.6 (~60% under,
    /// head and shoulders clear). The step drives the capsule Y so <c>WaterSurfaceY - feetY</c> approaches this
    /// fraction of body height via a critically-damped settle. Surface swim only (v1 does not dive), so this is the
    /// single resting waterline.</summary>
    public float SwimSurfaceSubmersionFraction { get; init; } = SwimSurfaceSubmersionFraction;

    /// <summary>Buoyancy settle stiffness (rad/s): the angular frequency of the critically-damped approach that
    /// eases the swimming capsule to its <see cref="SwimSurfaceSubmersionFraction"/> waterline. Default 8. The step
    /// uses the EXACT analytic critically-damped solution over the tick, so any stiffness is unconditionally stable
    /// regardless of dt (no oscillation, at most a single bounded settle dip under adverse entry velocity); larger =
    /// a snappier settle, smaller = a lazier bob.</summary>
    public float SwimBuoyancyStiffness { get; init; } = SwimBuoyancyStiffness;
}
