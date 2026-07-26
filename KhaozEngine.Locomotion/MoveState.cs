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
/// grounded-at-origin, zero-velocity state with no buffered jump, moving at unmodified speed
/// (<see cref="SpeedScale"/> 1).
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

    /// <summary>Storage for <see cref="SpeedScale"/>, held as the OFFSET FROM 1 (<c>scale - 1</c>) rather than the scale
    /// itself, which is the whole reason the multiplier is a property and not a plain public field like everything else
    /// here. A struct field cannot have a non-zero default, so a raw <c>SpeedScale</c> field would make
    /// <c>default(MoveState)</c> - and every one of the many existing <c>new MoveState { ... }</c> initializers that
    /// predate this feature - a character frozen at 0 m/s. Storing the offset makes the zero default mean "unmodified"
    /// (1.0, exactly, no rounding), so a pre-feature construction site stays bit-identical and a forgotten carry-through
    /// degrades to normal speed rather than to paralysis.</summary>
    private float speedScaleBias;

    /// <summary>Per-entity HORIZONTAL speed multiplier: haste (&gt; 1), slow (&lt; 1), root (exactly 0), unmodified
    /// (1, the default). It is a movement INPUT the step reads and carries through unchanged - the engine owns the
    /// scale and its plumbing, never the buff: duration, stacking, what granted it, and how it is balanced are game
    /// concerns. On a networked player it is authored solely by the server (<c>ShardedWorldServer.SetSpeedScale</c> /
    /// <c>WorldServer.SetSpeedScale</c>) and replicated as <c>MovementState.SpeedScaleQ</c>, so a hostile client cannot
    /// grant itself one and a mid-boost correction replays the pending command window at the boosted speed. On a
    /// server-only NPC or a single-player controller it is set directly and rides no wire.
    /// <para>Composes MULTIPLICATIVELY into the existing speed product rather than replacing any of it, so it stacks
    /// with the grounded/<see cref="MoveTuning.AirControl"/> term and the medium's wade scale. Consequences worth
    /// knowing: a hasted player who jumps travels correspondingly further horizontally (air control is scaled, jump
    /// HEIGHT is not - this is a horizontal scale only), and the boost persists into a swim
    /// (<see cref="MoveTuning.SwimSpeed"/> is scaled too, so diving mid-boost does not silently drop it).</para>
    /// Clamped to <c>&gt;= 0</c> on assignment: a negative multiplier would reverse travel against the command, which
    /// is never a movement modifier's job. There is no upper clamp here (a server-only NPC may use any speed), but the
    /// replicated range is bounded - see <c>MovementState.MaxSpeedScale</c>.</summary>
    public float SpeedScale
    {
        readonly get => 1f + speedScaleBias;
        set => speedScaleBias = (value > 0f ? value : 0f) - 1f;
    }

    /// <summary>CARRIED horizontal velocity in m/s (X = world X, Y = world Z), the airborne inertia the step flies the
    /// capsule along while <see cref="MoveTuning.AirMomentum"/> is on. It is maintained on EVERY tick regardless of that
    /// knob and CONSUMED only when the knob is on, which is what makes the "unchanged at the default" claim STRUCTURAL
    /// rather than behavioural: with momentum off the field is written and never read, so a game that never opts in
    /// cannot have its jump arc moved by the mere existence of this field. <c>default</c> (zero) is byte-identical to a
    /// pre-feature state and reads as "carrying nothing", so a state that never went through a step degrades to the old
    /// instant-to-target model rather than to a phantom drift.
    /// <para>What is stored is the step's INTENDED velocity CLIPPED to what the collision resolve actually delivered:
    /// projected along its own direction and clamped into <c>[0, |intended|]</c>. Free flight therefore leaves it
    /// untouched, a head-on wall clips it to ~0, a glancing wall sheds magnitude and keeps direction, and neither a
    /// depenetration nudge nor a play-area clamp can ever INJECT speed into it. That upper clamp is the whole reason
    /// the field is safe to carry tick-to-tick: nothing downstream of the command can grow it.</para>
    /// Replicated as <c>MovementState.HorizontalVelocityXQ</c> / <c>HorizontalVelocityZQ</c> in NetWorld, because
    /// <c>PlayerMoveState.From</c> rebuilds the whole reconcile basis from the replicated components ALONE: a carried
    /// field missing from that seed silently resets to the struct default on every correction and diverges mid-air the
    /// moment one lands, which is exactly the failure <c>SpeedScaleQ</c> was added to fix.</summary>
    public Vector2 HorizontalVelocity;

    /// <summary>SIM-LOCAL step OUTPUT (NOT replicated): the UNCONSTRAINED horizontal VELOCITY in m/s this step actually
    /// commanded, before the slope gate, static collision, and the play-area clamp denied any of it. Its magnitude is
    /// the whole speed product the step resolved - walk/run (or <see cref="MoveTuning.SwimSpeed"/> while swimming) times
    /// the grounded/<see cref="MoveTuning.AirControl"/> term, the medium's wade ramp and zone scale, the per-entity
    /// <see cref="SpeedScale"/>, and (on the world-space NPC path) the steering vector's speed fraction - so with
    /// nothing denying the move the step travels exactly <c>CommandedVelocity * dt</c>. <c>(0,0)</c> on an idle tick.
    /// <para>It exists so the server-side movement anomaly check can measure ONLY the denial. That check compares
    /// where the step landed against where the command intended to reach, and it used to REBUILD the intended
    /// target from <see cref="MoveTuning.WalkSpeed"/>/<see cref="MoveTuning.RunSpeed"/> alone: every speed term the
    /// step applied and the check did not know about read as a large correction on every tick, so a legitimately
    /// swimming, wading or zone-slowed player was reported as a speed hacker. Exporting the fact
    /// the sim already computed, instead of reconstructing it downstream, is the same fix the stair glide made when
    /// <see cref="ClimbRate"/> replaced a render-side position-delta estimator, and it means a future speed term
    /// cannot desync the anti-cheat again.</para>
    /// <para>It is a VECTOR and not a scalar for the same reason, one level up. The check pairs the exported number
    /// with the command's own DIRECTION, which stops being the direction of travel the moment
    /// <see cref="MoveTuning.AirMomentum"/> is on: a player who releases input mid-flight keeps travelling along the
    /// conserved <see cref="HorizontalVelocity"/>, so a scalar export would place the intended target back at the
    /// capsule and measure a legitimate arc as a full-speed denial on EVERY airborne tick. Exporting the velocity the
    /// step resolved, direction included, is exact under both models: with momentum off it is exactly
    /// <c>moveDir * CommandedSpeed</c>, so the check is arithmetically identical to the pre-momentum one.</para>
    /// Written every tick, so it is a per-tick FACT rather than carried state. <c>default</c> is <c>(0,0)</c>, which
    /// reads as "commanded nothing", the safe direction: a state that never went through a step measures as no denial
    /// rather than as a large one.</summary>
    public Vector2 CommandedVelocity;

    /// <summary>The magnitude of <see cref="CommandedVelocity"/>: the unconstrained horizontal SPEED in m/s this step
    /// commanded. Computed rather than stored, so there is exactly one number and the scalar view can never drift from
    /// the vector it is derived from. 0 on an idle tick.</summary>
    public readonly float CommandedSpeed => CommandedVelocity.Length();

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

    /// <summary>SIM-LOCAL smoothing state (NOT replicated): the exponentially-weighted moving average of the
    /// actually-applied per-tick climb RATE (<c>(pos.Y - prevY) / dt</c>) over a continuous paced stair run, in m/s.
    /// <see cref="ClimbRate"/> is stamped FROM this on a run tick, so the exported signal converges to the sim's own
    /// TRUE emergent rise rate (footprint-limited, co-paced) instead of the commanded rate - which is what drives the
    /// render-glide feed-forward/damp equilibrium offset to ~0 (a converged signal means render height == true feet on
    /// average, no half-riser hover and no crest snap). Updated ONLY on a detected continuous-run tick and reset to 0
    /// otherwise, so a fall / jump / flat / single-riser never accumulates into it (the fall-sink stays correct by
    /// construction). It is carried tick-to-tick like <see cref="VerticalVelocity"/> but rides no wire: MoveState is
    /// sim-local and only the derived <see cref="ClimbRate"/> (as <c>MovementState.ClimbRateQ</c>) replicates, and both
    /// heads run the same deterministic update so their EWMA - and therefore the stamped signal - agree by construction.
    /// <c>default</c> is 0 (byte-identical to a pre-feature state).</summary>
    public float ClimbRateEwma;

    /// <summary>SIM-LOCAL discrete-step impulse (NOT replicated): the vertical delta a DISCRETE step committed THIS tick,
    /// signed (positive = an isolated step-UP seat / the first riser of a run before the continuous climb signal engages;
    /// negative = an isolated step-DOWN grounded-hold seat; exactly 0 on every other tick). It is the authoritative FACT a
    /// mesh smoother reads to ease an isolated step the CONTINUOUS glide declines: the continuous glide
    /// (<see cref="ClimbRate"/>) renders raw whenever its signal is 0, so a one-tick isolated riser pops (a mini-teleport)
    /// unless a decaying MESH offset carries it. This field is that offset's feed: a client-side layer accumulates it into
    /// a render-time-decaying vertical offset subtracted from the drawn feet (UE-style step-event mesh smoothing).
    /// <para>MUTUALLY EXCLUSIVE with <see cref="ClimbRate"/> per tick: a CONTINUOUS run exports <see cref="ClimbRate"/> and
    /// leaves this 0 (the glide owns the smoothing, so the step-offset must not double-apply); a DISCRETE step exports this
    /// and leaves <see cref="ClimbRate"/> 0 (the glide renders raw and the mesh offset does the easing). Only the
    /// <c>steppedUp</c> seat and the step-down grounded-hold export it: a fall, a jump, a swim, a teleport, a terrain
    /// slope, and a flat tick all leave it 0, so a landing is never a step event (the fall-sink stays correct by
    /// construction).</para>
    /// It is zeroed every tick (<c>default</c> is 0) and set only on a commit tick, so it is a per-tick EVENT, not carried
    /// state. It rides NO wire (remotes soften a single step through their existing 2-tick position interpolation): only
    /// the local owner reads it, at the client-prediction Predict boundary (exactly-once per real tick, never re-counted on
    /// a reconciliation replay). <c>default</c> 0 is byte-identical to a pre-feature state.</summary>
    public float StepDeltaY;
}
