using System;

namespace KhaozEngine.Game
{
    /// <summary>Tunables for <see cref="ReplicatedCharacterAnimators"/>. <see cref="Locomotion"/> + <see cref="Crossfade"/>
    /// configure the per-entity <see cref="AnimatedCharacter"/> ONLY when the set builds it (the
    /// skeleton-plus-clips constructor); when you supply a <c>Func&lt;AnimatedCharacter&gt;</c> factory the brain you
    /// build owns its own thresholds/crossfade and these two fields are not applied. The remaining fields always
    /// govern the bridge's position-driven derivation.</summary>
    public struct CharacterAnimatorTuning
    {
        /// <summary>Speed thresholds for idle/walk/run. Applied to brains the set constructs (skeleton+clips ctor).
        /// Default <see cref="LocomotionThresholds.Default"/>.</summary>
        public LocomotionThresholds Locomotion;

        /// <summary>Crossfade seconds between locomotion clips. Applied to brains the set constructs. Default 0.15.</summary>
        public float Crossfade;

        /// <summary>Per-frame lerp factor (0..1) for turning the character toward its movement heading; higher turns
        /// faster. Default 0.2.</summary>
        public float YawSmoothing;

        /// <summary>Below this planar speed (m/s) the DERIVED facing yaw is held (no spin at rest). Ignored when a
        /// sample carries an explicit <see cref="CharacterSample.FacingYaw"/> - server-authoritative facing turns in
        /// place regardless of speed. Default 0.05.</summary>
        public float MinPlanarSpeedForFacing;

        /// <summary>When a sample carries no exact movement, |vertical velocity| below this (m/s) reads as grounded;
        /// above it the character is treated as airborne (jump/fall). Keeps small terrain-follow bumps grounded.
        /// Default 0.5.</summary>
        public float GroundedVerticalEpsilon;

        /// <summary>Uniform scale baked into <see cref="CharacterPose.World"/> so the consumer draws with that matrix
        /// directly. Default 1.</summary>
        public float Scale;

        /// <summary>Radians added to the facing yaw, whether it is derived from the heading OR supplied explicitly via
        /// <see cref="CharacterSample.FacingYaw"/>. The bridge faces an asset whose rest pose looks down +Z; set this
        /// (e.g. <see cref="MathF.PI"/>) for an asset authored facing another axis. Default 0.</summary>
        public float FacingYawOffset;

        /// <summary>Length (seconds) of the sliding window the bridge averages position displacement over to derive
        /// velocity, instead of using a single frame's delta. This makes the derived speed frame-rate independent and
        /// robust to ZERO-DELTA frames: <c>ClientPrediction.RenderedState</c> plateaus once inter-tick interpolation
        /// saturates (the rendered position is constant between server ticks), so whenever render fps &gt; tick rate
        /// some frames have no position change; a single-frame derivation reads speed 0 on those frames and strobes
        /// the locomotion state Idle&lt;-&gt;moving (which restarts the clip every frame). Averaging over ~1 tick holds
        /// the last good velocity across the plateau. Set to one tick of the source (default 1/30 s); a genuine stop
        /// still resolves to Idle within one window. &lt;= 0 reverts to per-frame derivation. Default 1/30.</summary>
        public float VelocityWindowSeconds;

        /// <summary>Seconds a newly-evaluated GROUND state (idle/walk/run) must persist before the brains this set
        /// builds switch to it - passed to <see cref="AnimatedCharacter"/> as its <c>stateDebounceSeconds</c>. The
        /// derived speed still ripples a little even after windowing (the prediction/reconcile render stream is not
        /// perfectly smooth, and a remote's replicated position arrives as a ~30 Hz staircase), so without a debounce
        /// the state chatters across a band threshold and restarts the clip every few seconds (the "stutter"). Air
        /// states (jump/fall) are exempt and switch instantly. Applied to brains the set CONSTRUCTS (the skeleton+clips
        /// ctor); a <c>Func&lt;AnimatedCharacter&gt;</c> factory owns its own debounce. Default
        /// <see cref="AnimatedCharacter.DefaultStateDebounceSeconds"/>; 0 = switch immediately.</summary>
        public float StateDebounceSeconds;

        /// <summary>Opt-in: sync each ground MOVE clip's playback to the character's actual speed so its feet stop
        /// sliding ("gliding"). Applied to brains the set CONSTRUCTS (the skeleton+clips ctor) via
        /// <see cref="LocomotionSpeedSync"/>; a <c>Func&lt;AnimatedCharacter&gt;</c> factory owns its own sync config.
        /// Requires <see cref="WalkClipSpeed"/> / <see cref="RunClipSpeed"/> to be set. Default false (playback
        /// unchanged - every existing consumer is byte-identical until it opts in).</summary>
        public bool SyncLocomotionToSpeed;

        /// <summary>World speed (m/s) the Walk clip was authored to move at. Only used when
        /// <see cref="SyncLocomotionToSpeed"/> is set; 0 plays Walk at 1x. Default 0.</summary>
        public float WalkClipSpeed;

        /// <summary>World speed (m/s) the Run clip was authored to move at. Only used when
        /// <see cref="SyncLocomotionToSpeed"/> is set; 0 plays Run at 1x. Default 0.</summary>
        public float RunClipSpeed;

        /// <summary>World speed (m/s) the forward <see cref="LocomotionState.Swim"/> clip was authored to move at.
        /// Only used when <see cref="SyncLocomotionToSpeed"/> is set; 0 plays Swim at 1x. Default 0.</summary>
        public float SwimClipSpeed;

        /// <summary>Lower clamp on the speed-sync playback multiplier (keeps a near-stationary entity from freezing
        /// the clip). Only used when <see cref="SyncLocomotionToSpeed"/> is set; 0 uses
        /// <see cref="LocomotionSpeedSync.DefaultMinMultiplier"/>. Default 0.25.</summary>
        public float MinLocomotionRate;

        /// <summary>Upper clamp on the speed-sync playback multiplier (keeps a teleporting entity from fast-forwarding
        /// the clip). Only used when <see cref="SyncLocomotionToSpeed"/> is set; 0 uses
        /// <see cref="LocomotionSpeedSync.DefaultMaxMultiplier"/>. Default 3.0.</summary>
        public float MaxLocomotionRate;

        /// <summary>Opt-in: when a sample's <see cref="CharacterSample.Sector"/> is
        /// <see cref="KhaozEngine.Locomotion.MoveSector.Reverse"/>, play its move clip BACKWARDS at the speed-matched
        /// rate instead of forwards, so a backpedal strides backwards rather than moonwalking (the body slides back
        /// while the feet stride forward). Only the playback DIRECTION changes: the locomotion state is still picked
        /// from the speed magnitude, so a reverse walk is the walk clip and a reverse run is the run clip. Rides the
        /// speed sync, so it needs <see cref="SyncLocomotionToSpeed"/> too, and applies to brains the set CONSTRUCTS
        /// (the skeleton+clips ctor); a <c>Func&lt;AnimatedCharacter&gt;</c> factory sets
        /// <see cref="LocomotionSpeedSync.ReverseOnReverseSector"/> on its own sync config instead.
        ///
        /// <para>Default false, so every existing consumer is byte-identical until it opts in - and even opted in,
        /// nothing changes for a consumer that never classifies a sector, since every sample defaults to
        /// <see cref="KhaozEngine.Locomotion.MoveSector.Forward"/>.</para></summary>
        public bool ReverseLocomotionOnReverseSector;

        /// <summary>Critical-damp settle rate (radians/second) of the SIGNAL-GATED render-height glide that makes a stair
        /// climb read as a smooth glide up the stair slope instead of a per-riser bob. The glide engages iff the sample
        /// carries a non-zero sim-exported climb rate (<see cref="CharacterSample.ClimbRate"/> - the fact the simulation
        /// stamps, never a position-delta estimate): <see cref="ReplicatedCharacterAnimators.Update"/> feeds that exact
        /// signed rate forward (<c>SmoothedY += ClimbRate * dt</c>, lag-free ramp tracking) and then critically damps
        /// SmoothedY toward the true feet-Y at THIS rate to absorb quantization drift and settle onto real tread tops. The
        /// smoothed height is baked into <see cref="CharacterPose.World"/> (the drawn feet) and, lifted to the capsule
        /// centre, exposed as <see cref="CharacterPose.CameraTarget"/> (point a follow camera at that, NOT the
        /// feet-anchored <see cref="CharacterPose.RenderPosition"/>).
        /// <para>Default 5 (rad/s). Derivation: it is now the ONLY smoothing term (the feed-forward is the exact sim rate,
        /// so the damp only has to absorb quantization drift and the remote interpolation-vs-quantized-rate mismatch, not a
        /// per-riser sawtooth), and it still settles a mid-stair rest offset onto the tread in about 0.8 s. On flat ground
        /// - and during any fall, jump, teleport, or platform ride - the sim stamps <see cref="CharacterSample.ClimbRate"/>
        /// == 0, so the glide never engages and render-Y equals the true feet-Y byte-for-byte (identity, correct by
        /// construction, no fall-sink possible). <b>Set &lt;= 0 to disable</b> the glide entirely (render-Y is always the
        /// true feet-Y, byte-identical to the pre-feature bridge).</para></summary>
        public float SlopeGlideRate;

        /// <summary>Render-height gap (metres) beyond which the slope-fed smoother SNAPS the smoothed feet-Y to the true
        /// feet-Y instead of gliding - a fall, a jump takeoff, a ledge walk-off, or a LARGE teleport should be crisp, not
        /// crawl up over a fraction of a second. Mirrors <see cref="CharacterAvatar.RenderHeightSnapDistance"/>: default
        /// 1.5, well above any single stair riser (0.30) and below a floor-to-floor jump. A teleport whose vertical gap
        /// exceeds this snaps same-frame automatically; a SHORT teleport under it is height-identical to a stair riser,
        /// so the hard cut for those comes from <see cref="ReplicatedCharacterAnimators.SnapRenderHeight"/> (the consumer
        /// hook wired to the netcode teleport epoch), not this gap. Only consulted when
        /// <see cref="SlopeGlideRate"/> &gt; 0. Also the safety bound on the DISCRETE-STEP mesh offset: a per-frame
        /// step-cumulative jump larger than this is not a real step (a teleport / reconnect re-baseline slipped through),
        /// so the mesh offset hard-cuts instead of easing it.</summary>
        public float SlopeGlideSnapDistance;

        /// <summary>Exponential decay rate (1/second) of the DISCRETE-STEP MESH offset - the UE-style step-event smoothing
        /// that eases an ISOLATED step (a building doorstep, a curb, the first riser of a run before the continuous climb
        /// signal engages, or an isolated step-down) the continuous <see cref="SlopeGlideRate"/> glide declines. The sim
        /// commits such a step's full rise/drop in one (or a few) tick(s) and, because it exports no continuous climb rate
        /// for a single riser (<see cref="CharacterSample.ClimbRate"/> == 0), the glide renders it RAW - so the drawn feet
        /// pop the whole step in one frame (a mini-teleport). This layer instead FREEZES the mesh at its previous drawn
        /// height on the step tick (from the sim's exported per-tick step impulse <see cref="CharacterSample.StepCumulativeY"/>,
        /// diffed to detect a step exactly once) and decays that freeze offset to zero: the mesh starts at the pre-step
        /// height and eases up (or down) to the true feet. The freeze (rather than adding the raw impulse) absorbs the
        /// inter-tick-interpolation phase mismatch, so the mesh never overshoots past the pre-step (see the smoother in
        /// <see cref="ReplicatedCharacterAnimators.Update"/>).
        /// <para>Default 30 (1/s). Derivation: the freeze offset decays as <c>offset(t) = offset0 * e^(-rate*t)</c> (frame-
        /// rate independent). At <c>rate = 30</c>, a typical ~0.2 m doorstep decays to sub-perceptual (~5 mm,
        /// <c>0.2 * e^(-30*0.12) ~= 5 mm</c>) by ~120 ms and a MAX riser (~0.35 m) by ~140 ms, while the half-life
        /// <c>ln2/30 ~= 23 ms</c> keeps the ease a smooth soft settle rather than a lag. Gentler than a fast-settle rate
        /// (40+/s) so the ease reads as smoothing, not a quick catch-up; a slower rate trades a longer tail for an even
        /// softer leading edge, a taste call best confirmed in-game. It composes with the continuous glide by construction:
        /// the sim stamps EITHER a continuous ClimbRate OR a discrete step impulse per tick (never both), so a continuous
        /// run leaves this offset untouched (it just decays), and the first riser's offset decays out as the glide takes
        /// over. A teleport (<see cref="ReplicatedCharacterAnimators.SnapRenderHeight"/>) or a step cumulative jump beyond
        /// <see cref="SlopeGlideSnapDistance"/> zeroes it (hard cut). <b>Set &lt;= 0 to disable</b> the step smoothing
        /// (isolated steps render raw, byte-identical to the pre-feature bridge).</para></summary>
        public float StepSmoothingRate;

        /// <summary>Seconds the PROCEDURAL downed collapse takes to tip the body from upright to fully prone, used ONLY
        /// when a downed entity's rig has no <see cref="LocomotionState.Downed"/> clip (a rig WITH one plays that clip
        /// instead, on its own timing). Over this duration the bridge rotates the drawn model about its facing-lateral
        /// axis through a smooth (smoothstep) ramp from 0 to 90 degrees so it topples forward in its facing direction
        /// and lies flat, pivoting at the feet so the whole body settles at ground level (not floating at capsule
        /// centre). It then HOLDS prone until the downed flag clears. Default 0.5 s (a quick, readable knockdown).
        /// &lt;= 0 snaps to prone on the first downed frame (no ramp). Unused by an entity never marked
        /// <see cref="CharacterSample.Downed"/>, so it never affects existing rendering.</summary>
        public float DownedCollapseSeconds;

        /// <summary>The <see cref="LocomotionSpeedSync"/> these fields describe, applied to brains this set
        /// constructs. Disabled unless <see cref="SyncLocomotionToSpeed"/> is set.</summary>
        public readonly LocomotionSpeedSync SpeedSync() => SyncLocomotionToSpeed
            ? LocomotionSpeedSync.Enable(WalkClipSpeed, RunClipSpeed,
                MinLocomotionRate > 0f ? MinLocomotionRate : LocomotionSpeedSync.DefaultMinMultiplier,
                MaxLocomotionRate > 0f ? MaxLocomotionRate : LocomotionSpeedSync.DefaultMaxMultiplier,
                SwimClipSpeed, ReverseLocomotionOnReverseSector)
            : LocomotionSpeedSync.Disabled;

        public static CharacterAnimatorTuning Default => new CharacterAnimatorTuning
        {
            Locomotion = LocomotionThresholds.Default,
            Crossfade = 0.15f,
            YawSmoothing = 0.2f,
            MinPlanarSpeedForFacing = 0.05f,
            GroundedVerticalEpsilon = 0.5f,
            Scale = 1f,
            FacingYawOffset = 0f,
            VelocityWindowSeconds = 1f / 30f,
            StateDebounceSeconds = AnimatedCharacter.DefaultStateDebounceSeconds,
            SyncLocomotionToSpeed = false,
            WalkClipSpeed = 0f,
            RunClipSpeed = 0f,
            SwimClipSpeed = 0f,
            MinLocomotionRate = LocomotionSpeedSync.DefaultMinMultiplier,
            MaxLocomotionRate = LocomotionSpeedSync.DefaultMaxMultiplier,
            ReverseLocomotionOnReverseSector = false,
            SlopeGlideRate = DefaultSlopeGlideRate,
            SlopeGlideSnapDistance = DefaultSlopeGlideSnapDistance,
            StepSmoothingRate = DefaultStepSmoothingRate,
            DownedCollapseSeconds = DefaultDownedCollapseSeconds,
        };

        /// <summary>Default <see cref="SlopeGlideRate"/> (rad/s): 5. See that field for the derivation.</summary>
        public const float DefaultSlopeGlideRate = 5f;

        /// <summary>Default <see cref="SlopeGlideSnapDistance"/> (metres): 1.5.</summary>
        public const float DefaultSlopeGlideSnapDistance = 1.5f;

        /// <summary>Default <see cref="StepSmoothingRate"/> (1/s): 30. See that field for the derivation.</summary>
        public const float DefaultStepSmoothingRate = 30f;

        /// <summary>Default <see cref="DownedCollapseSeconds"/> (seconds): 0.5.</summary>
        public const float DefaultDownedCollapseSeconds = 0.5f;
    }
}
