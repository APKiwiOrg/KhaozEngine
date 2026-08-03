using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 6, run 12, half-height 0.9 for a 1.8 m capsule, 45 deg max slope,
/// footprint radius 0.4 for static-world collision) plus the vertical-physics feel (gravity 25, jump 9.79796,
/// terminal 50, 0.1 s coyote + buffer, full air control, 0.3 m grounded skin, airborne momentum OFF, facing
/// turn rate infinite so the heading snaps) plus the steep-terrain feel (3 deg traction hysteresis, 8 deg slide
/// friction ramp).
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
    float MaxStepClimbSpeed = 3.5f,
    bool AirMomentum = false,
    float AirBrakeAccel = 0f,
    float FacingTurnSpeed = float.PositiveInfinity,
    float TractionHysteresisRadians = MathF.PI * 3f / 180f,
    float SlideFrictionRampRadians = MathF.PI * 8f / 180f)
{
    /// <summary>Walkable-slice defaults: walk 6 m/s, run 12 m/s, capsule half-height 0.9 m, max slope 45 deg
    /// (steep enough for normal hills, low enough that a RimFeature mountain wall is too steep to stand on, so the
    /// rim stays un-climbable when a <c>groundNormal</c> delegate is supplied), capsule footprint radius
    /// 0.4 m used by static-world collision, plus vertical physics: gravity 25 m/s^2, jump launch 9.79796 m/s
    /// (= 8 * sqrt(1.5), +50% apex vs the old 8f: apex ~1.92 m, matching Ruinborne's deliberate jump-height
    /// value), terminal fall 50 m/s, 0.1 s coyote-time + jump-buffer, full (1.0) air control, and a 0.3 m
    /// grounded skin so a downhill run does not jitter between grounded and airborne, and airborne momentum OFF
    /// (<see cref="AirMomentum"/> false, <see cref="AirBrakeAccel"/> 0), so a jump arc behaves exactly as it did
    /// before momentum existed, and an infinite <see cref="FacingTurnSpeed"/>, so the heading snaps to its target
    /// exactly as a pre-facing consumer's commanded-facing presentation did. This matches
    /// CharacterController3D's own field defaults exactly (same literal + comment in both places), so a caller
    /// building either way gets identical feel.
    /// <para>The steep-terrain pair comes with it and is default-ON: a 3 degree
    /// <see cref="TractionHysteresisRadians"/> band, so a walk across a bank that straddles the gate holds one
    /// continuous footing decision instead of flipping every tick, and an 8 degree
    /// <see cref="SlideFrictionRampRadians"/>, so a face a degree past the gate slides gently and only a genuinely
    /// steep one slides at full gravity. Both are new in 17.30.0 and both CHANGE behaviour near the gate for any game
    /// on steep terrain, which is intended (see #475). Setting either to 0 restores the previous behaviour
    /// exactly.</para></summary>
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

    /// <summary>Master opt-in for AIRBORNE horizontal momentum. Default false, which is today's model exactly: a
    /// character in free flight has no inertia and its horizontal is recomputed from the command every tick, so a
    /// mid-air <see cref="MoveState.SpeedScale"/> change collapses a committed arc and releasing input stops horizontal
    /// travel dead. With it on, the airborne step instead flies the carried <see cref="MoveState.HorizontalVelocity"/>,
    /// steering it toward the commanded velocity with <see cref="AirControl"/> as the steering authority: a jump at
    /// speed S travels its whole arc at S whatever the command does afterwards, and input can only ever ACCELERATE the
    /// arc, never brake it below the conserved speed (that is what <see cref="AirBrakeAccel"/> is for).
    /// <para>GROUNDED motion is untouched either way. Momentum on the ground would change the feel of every game on the
    /// stack, so it is a separate and later decision. The off default is the load-bearing part of this knob rather than
    /// a formality: the fleet has been burned once by an engine bump silently retuning inherited tuning defaults with a
    /// green build, so a game that does not set this must be bit-identical to the release before it.</para>
    /// It also gives <see cref="AirControl"/> a meaning it never had. At 0 the airborne step is a true BALLISTIC arc
    /// (input ignored, the arc flies out) rather than "frozen horizontally in mid-air", and at 1 it is full authority
    /// over the direction of travel at the conserved speed (an instant 180 mid-flight, still at speed). Both readings
    /// are strictly behind this opt-in.</summary>
    public bool AirMomentum { get; init; } = AirMomentum;

    /// <summary>Rate (m/s^2) at which a conserved airborne speed BLEEDS DOWN toward a slower commanded speed while
    /// <see cref="AirMomentum"/> is on. Default 0, which is pure conservation: an arc launched at 30 m/s stays at
    /// 30 m/s until it lands, however slow the command underneath it becomes. A positive value decays the conserved
    /// speed by <c>AirBrakeAccel * dt</c> per tick and STOPS at the commanded speed, never below it, so the arc settles
    /// onto what the character can actually move at instead of overshooting into a reverse.
    /// <para>It exists for two reasons. A root, a snare, or a slow landing mid-flight is a real case a game may want to
    /// bleed a committed arc for rather than honour to its end, and it is the one knob that lets a game dial back
    /// toward the pre-momentum feel without turning momentum off entirely (a large value collapses the arc to the
    /// command within a tick or two). Ignored entirely while <see cref="AirMomentum"/> is off.</para></summary>
    public float AirBrakeAccel { get; init; } = AirBrakeAccel;

    /// <summary>Maximum rate (RADIANS PER SECOND) at which <see cref="MoveState.FacingYaw"/> turns toward its target -
    /// the camera yaw while <see cref="MoveCommand.FaceCamera"/> is held, otherwise the yaw of the commanded travel
    /// direction. The turn always takes the SHORTEST ARC and lands exactly on the target on the tick the last of the
    /// gap fits inside one step's budget, so a rate changes how long a turn takes and never where it ends.
    /// <para>Default <see cref="float.PositiveInfinity"/>, which SNAPS the heading to the target in one tick. That is
    /// deliberately the default rather than some plausible finite rate: before facing became authoritative state, a
    /// consumer pointed its model straight at <see cref="CharacterMovement.CameraRelativeDir"/> with no smoothing at
    /// all, so an infinite rate is the presentation feel every existing game already has. A game that wants a body
    /// that leans into its turns sets a finite value (2-10 rad/s is the usual range) and gets it identically on the
    /// server, in client prediction, and on every remote, because the turn is part of the authoritative step rather
    /// than a presentation smoother each end runs its own version of.</para>
    /// A value of 0 (which is what a struct <c>default(MoveTuning)</c> reads, exactly as it reads 0 for
    /// <see cref="WalkSpeed"/>) FREEZES the heading. That is the harmless degradation for an accidental default:
    /// treating 0 as "no limit" would make the un-configured case the most aggressive setting there is.</summary>
    public float FacingTurnSpeed { get; init; } = FacingTurnSpeed;

    /// <summary>TRACTION HYSTERESIS BAND (radians): how far past <see cref="MaxSlopeRadians"/> a character that ALREADY
    /// has footing keeps it. Footing is GRANTED at <see cref="MaxSlopeRadians"/> and KEPT to
    /// <c>MaxSlopeRadians + this</c>, so the walkability decision has a memory instead of being re-taken from scratch
    /// every tick. Default 3 degrees.
    /// <para>It exists because a bare threshold chatters on real terrain, which was measured rather than argued: a
    /// Ruinborne bank whose columns run 40.0 to 41.8 degrees against a 40 degree gate flipped footing 43 times in
    /// 330 ticks and stalled part-way up, and re-tuning the gate to clear that bank simply moved the same failure onto
    /// banks peaking 0.4 and 2.8 degrees past the new one. 3 degrees covers every straddle in those measurements (the
    /// worst excursion above a gate was 2.8 degrees) with margin to spare, and stays small enough that the ground a
    /// standing character may keep its feet on is still ground a player reads as a steep hillside.</para>
    /// <para>The band is a ceiling on what footing may KEEP and never a route to footing: a character with no footing
    /// is judged at the bare <see cref="MaxSlopeRadians"/>, so landing on, sliding onto, or grazing ground past the
    /// gate grants nothing. The steepest ground a character can end up standing on is therefore exactly
    /// <c>MaxSlopeRadians + this</c>, by any route. One play consequence of that surprises and is correct: on ground
    /// only the band is holding, a JUMP converts a stable stand into a slide, because the launch tick ends
    /// un-grounded and the landing is judged at the bare <see cref="MaxSlopeRadians"/>.</para>
    /// <para>0 (which is what a struct <c>default(MoveTuning)</c> reads, exactly as it reads 0 for
    /// <see cref="WalkSpeed"/>) turns hysteresis OFF and restores the bare per-tick threshold of every release before
    /// 17.30.0. A negative or NaN value reads the same way. That is the harmless degradation for an accidental
    /// default, in the same direction <see cref="FacingTurnSpeed"/> takes.</para></summary>
    public float TractionHysteresisRadians { get; init; } = TractionHysteresisRadians;

    /// <summary>SLIDE FRICTION RAMP (radians): the band past <see cref="MaxSlopeRadians"/> over which a slide's
    /// fall-line acceleration ramps in, from nothing at the gate to full gravity at <c>MaxSlopeRadians + this</c> and
    /// beyond. The scale is <c>clamp((surface slope - MaxSlopeRadians) / this, 0, 1)</c> on the DOWNHILL half of
    /// gravity only. Default 8 degrees.
    /// <para>Without it every surface past the gate slides at the full fall-line projection of gravity, so ground one
    /// degree too steep to stand on behaves exactly like an 80 degree cliff and marginal terrain reads as ice. With
    /// it, at the shipped 45 degree gate, a 46 degree face accelerates at an eighth of gravity's pull along the fall
    /// line (2.25 m/s^2 at the default gravity, against 17.99 unscaled) and is a slope you lose ground on slowly,
    /// while anything at 53 degrees or steeper is unchanged. 8 degrees is chosen so the whole band sits inside terrain
    /// a player already reads as unwalkable, rather than reaching up into the slopes hysteresis is holding footing
    /// on.</para>
    /// <para>It scales the ACCELERATION, not the speed, so it is not a terminal: a long enough marginal face still
    /// reaches <see cref="MaxFallSpeed"/> eventually, over a much greater distance. And it never applies to a RISING
    /// slide, where gravity keeps its full strength, so the height a launch can ride up a face is exactly what it was
    /// before friction existed (see <c>CharacterMovement.Traction.cs</c>, which is where scaling both halves would
    /// have been an altitude exploit).</para>
    /// <para>0 (a <c>default(MoveTuning)</c>), negative, or NaN turns friction OFF and restores the full-strength
    /// slide of 17.28.0 and 17.29.0 bit for bit.</para></summary>
    public float SlideFrictionRampRadians { get; init; } = SlideFrictionRampRadians;
}
