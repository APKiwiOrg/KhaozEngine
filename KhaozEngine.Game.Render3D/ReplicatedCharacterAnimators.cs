using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
    /// <summary>One visible entity's movement sample for <see cref="ReplicatedCharacterAnimators"/>, one per frame.
    /// Deliberately engine-neutral (no netcode type): a networked game maps its per-entity render state
    /// (e.g. <c>KhaozEngine.NetWorld.EntityRenderState</c>) to this in a tiny loop, so the bridge stays usable by any
    /// game and the <c>Game.Render3D</c> package keeps its layering (no dependency on a netcode package).
    ///
    /// The only universally-available signal is <see cref="Position"/> over time, so by default the bridge DERIVES
    /// planar speed, vertical velocity, and facing from successive samples (averaged over a short window - see
    /// <see cref="CharacterAnimatorTuning.VelocityWindowSeconds"/> - so a plateauing position stream does not strobe
    /// the state). For the local player (whose exact movement the client already knows) pass the exact-movement
    /// constructor so <see cref="HasMovement"/> is set and the grounded flag + vertical velocity are taken verbatim
    /// instead of derived; the fullest constructor additionally takes the exact planar speed
    /// (<see cref="CharacterSample.PlanarSpeed"/>) so the locomotion state is driven by the clean commanded speed, not
    /// finite-differenced from the render position (no walk&lt;-&gt;idle flicker on a decel-to-stop).
    ///
    /// Facing is derived from the position delta by default, but a sample may carry an EXPLICIT server-authoritative
    /// facing yaw (<see cref="FacingYaw"/>, set via the position+facing constructor or <see cref="WithFacingYaw"/>).
    /// When present it overrides the derived heading and turns the character in place even while stationary - for a
    /// server-owned NPC tracking a target at melee range, a turret, a mount, or a player standing still and turning,
    /// whose facing the server knows every tick but a position delta cannot reveal at rest.</summary>
    public readonly struct CharacterSample
    {
        // Full-field constructor (private) backing WithFacingYaw / WithDowned: copies every field and overrides one.
        // Keeping it private avoids a public 13-arg overload; the public constructors below stay the documented surface.
        CharacterSample(long id, Vector3 position, bool isLocal, bool hasMovement, bool grounded, float verticalVelocity,
            bool swimming, float climbRate, bool hasPlanarSpeed, float planarSpeed, float? facingYaw, float stepCumulativeY, bool downed)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = hasMovement;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = hasPlanarSpeed;
            PlanarSpeed = planarSpeed;
            FacingYaw = facingYaw;
            StepCumulativeY = stepCumulativeY;
            Downed = downed;
        }

        /// <summary>Position-only sample: speed, vertical velocity, grounded, and swimming are all derived from the
        /// position delta vs the previous frame (swimming cannot actually be derived from position, so it reads false -
        /// pass an exact-movement constructor to animate swimming).</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = false;
            Grounded = false;
            VerticalVelocity = 0f;
            Swimming = false;
            ClimbRate = 0f;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
            StepCumulativeY = 0f;
            Downed = false;
        }

        /// <summary>Sample with exact movement (the local player, or any entity whose replicated <c>MovementState</c>
        /// is available): <see cref="Grounded"/>, <see cref="VerticalVelocity"/>, <see cref="Swimming"/>, and
        /// <see cref="ClimbRate"/> are used as given instead of being derived. <paramref name="swimming"/> and
        /// <paramref name="climbRate"/> default to the non-swimming / not-climbing values so a pre-swim / pre-glide
        /// caller is unchanged.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, bool swimming = false, float climbRate = 0f, float stepCumulativeY = 0f)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
            StepCumulativeY = stepCumulativeY;
            Downed = false;
        }

        /// <summary>Fullest sample (the local player): exact <see cref="Grounded"/>, <see cref="VerticalVelocity"/>,
        /// AND exact planar <paramref name="planarSpeed"/> (m/s). The planar speed drives the locomotion state and the
        /// clip-speed sync DIRECTLY instead of being finite-differenced from the rendered position. Pass the clean
        /// commanded speed (<c>WorldClient.LocalHorizontalSpeed</c> / <c>ClientPrediction.PredictedHorizontalSpeed</c>):
        /// it is computed only on the prediction's commanded path, so it does not carry the reconciliation render offset
        /// and does not strobe walk&lt;-&gt;idle when the player decelerates to a stop (where the rendered position, even
        /// after the C1 smoothing fix, settles with a tiny residual sag). Facing still follows the derived heading
        /// (planar speed is magnitude-only). A negative value is treated as zero. <paramref name="stepCumulativeY"/> is the
        /// local player's discrete-step accumulator (<c>ClientPrediction.StepCumulativeY</c> via
        /// <c>EntityRenderState.StepCumulativeY</c>); the bridge diffs it to ease an isolated step the continuous glide
        /// renders raw. Defaults to 0 (no mesh offset), so a caller that never supplies it is unchanged.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, float planarSpeed, bool swimming = false, float climbRate = 0f, float stepCumulativeY = 0f)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = true;
            PlanarSpeed = planarSpeed;
            FacingYaw = null;
            StepCumulativeY = stepCumulativeY;
            Downed = false;
        }

        /// <summary>Position + EXPLICIT facing sample: the position drives the derived planar speed / air state as the
        /// position-only constructor does, but the facing yaw is taken as authoritative (see <see cref="FacingYaw"/>)
        /// instead of derived from the heading. The simplest way to face a purely position-streamed entity where the
        /// server owns the facing (a turret, an NPC standing still at melee range). <paramref name="facingYaw"/> is a
        /// world yaw in radians about +Y (0 faces +Z; <see cref="CharacterAnimatorTuning.FacingYawOffset"/> still
        /// composes). To attach explicit facing to an EXACT-movement sample (grounded / vertical / planar speed /
        /// swimming) instead, build that sample and call <see cref="WithFacingYaw"/>.</summary>
        public CharacterSample(long id, Vector3 position, float facingYaw, bool isLocal = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = false;
            Grounded = false;
            VerticalVelocity = 0f;
            Swimming = false;
            ClimbRate = 0f;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = facingYaw;
            StepCumulativeY = 0f;
            Downed = false;
        }

        /// <summary>Stable per-entity key (e.g. <c>NetId.Value</c>, 64-bit since 10.0.0). Identifies the brain across frames.</summary>
        public long Id { get; }

        /// <summary>World position this frame (the only signal every netcode surfaces for every entity).</summary>
        public Vector3 Position { get; }

        /// <summary>True for the local (predicted) player, false for replicated remotes. Forwarded to the pose for
        /// the consumer (e.g. tinting / debug), never changes the brain's behaviour.</summary>
        public bool IsLocal { get; }

        /// <summary>True when this sample carries exact <see cref="Grounded"/> + <see cref="VerticalVelocity"/>
        /// (use them instead of deriving).</summary>
        public bool HasMovement { get; }

        /// <summary>Exact grounded flag (only meaningful when <see cref="HasMovement"/>).</summary>
        public bool Grounded { get; }

        /// <summary>Exact vertical velocity, m/s positive up (only meaningful when <see cref="HasMovement"/>).</summary>
        public float VerticalVelocity { get; }

        /// <summary>Exact surface-swimming flag (only meaningful when <see cref="HasMovement"/>). When set, the brain
        /// plays the tread <see cref="LocomotionState.SwimIdle"/> or forward <see cref="LocomotionState.Swim"/> clip
        /// (from the planar speed) instead of a ground/air state - swim wins over grounded/vertical. Sourced from the
        /// replicated <c>MovementState.Swimming</c> bit (via <c>EntityRenderState.Swimming</c>), never derived from
        /// position (a swimmer glides horizontally like a walker, so position cannot distinguish the two).</summary>
        public bool Swimming { get; }

        /// <summary>Exact signed step-climb rate in m/s (only meaningful when <see cref="HasMovement"/>): +ascending a
        /// paced stair run, -descending stepped risers, 0 not on a step climb. The presentation smoother in
        /// <see cref="ReplicatedCharacterAnimators.Update"/> glides the drawn feet up/down the stair slope iff this is
        /// non-zero, feeding it forward directly (never estimating climb from a position delta). Sourced from the sim's
        /// own <c>MoveState.ClimbRate</c> (local: predicted; remote: the decoded, nearest-sampled replicated
        /// <c>MovementState.ClimbRateQ</c>, via <c>EntityRenderState.ClimbRate</c>). 0 on every position-only sample.</summary>
        public float ClimbRate { get; }

        /// <summary>True when this sample carries an exact planar <see cref="PlanarSpeed"/> to use for the locomotion
        /// state instead of deriving it from the position delta.</summary>
        public bool HasPlanarSpeed { get; }

        /// <summary>Exact planar (ground-plane) speed, m/s (only meaningful when <see cref="HasPlanarSpeed"/>). Drives
        /// the idle/walk/run state and the clip-speed sync; facing uses the derived heading UNLESS <see cref="FacingYaw"/>
        /// is supplied.</summary>
        public float PlanarSpeed { get; }

        /// <summary>Optional EXPLICIT facing yaw (world radians about +Y, 0 faces +Z), or null to derive facing from the
        /// position delta as before. When set, <see cref="ReplicatedCharacterAnimators.Update"/> aims the character at
        /// this yaw plus <see cref="CharacterAnimatorTuning.FacingYawOffset"/> through the existing
        /// <see cref="CharacterAnimatorTuning.YawSmoothing"/> smoothing, so it turns in place even while stationary and
        /// WINS over the position-derived heading even while moving (server authority over derivation). Null on every
        /// existing constructor, so a caller that never supplies it behaves exactly as before. Sourced from whatever
        /// yaw the game replicates for the entity (e.g. a per-entity facing component on a server-owned NPC).</summary>
        public float? FacingYaw { get; }

        /// <summary>The local player's discrete-step accumulator (only meaningful when <see cref="HasMovement"/> and
        /// <see cref="IsLocal"/>): the session-monotonic running sum of committed isolated-step vertical impulses (from
        /// <c>ClientPrediction.StepCumulativeY</c> via <c>EntityRenderState.StepCumulativeY</c>). The bridge DIFFS it
        /// frame-to-frame to pick up each new isolated step-up/step-down EXACTLY ONCE and ease it with a render-time-
        /// decaying MESH offset (<see cref="CharacterAnimatorTuning.StepSmoothingRate"/>), softening the mini-teleport pop
        /// the continuous glide (which renders such singles raw) leaves. 0 on remotes (their singles are softened by
        /// position interpolation) and on every position-only sample, so no offset accumulates there.</summary>
        public float StepCumulativeY { get; }

        /// <summary>True when the game considers this entity DOWNED (dead / knocked out) this frame, so the bridge shows
        /// a downed pose instead of locomotion. The engine knows NOTHING about HP or death rules: a networked game
        /// DERIVES this client-side from state it already replicates (e.g. <c>hp &lt;= 0</c> off a replicated Hp
        /// component) with no wire change and sets it per frame. While set, <see cref="ReplicatedCharacterAnimators"/>
        /// suppresses locomotion (idle/walk/run, air, swim, and stacked action one-shots) for this entity and either
        /// plays the entity's baked <see cref="LocomotionState.Downed"/> clip once holding its final frame, or - with no
        /// such clip - collapses the body procedurally to prone over
        /// <see cref="CharacterAnimatorTuning.DownedCollapseSeconds"/> and settles it at ground level. Clearing it
        /// (respawn) returns the entity to normal locomotion. A respawn usually teleports, so pair the clear with
        /// <see cref="ReplicatedCharacterAnimators.SnapRenderHeight"/> for a crisp cut (no glide from the corpse
        /// position, no prone residual). Defaults false on every constructor, so a sample never marked downed renders
        /// exactly as before. Set it orthogonally on ANY sample shape via <see cref="WithDowned"/>.</summary>
        public bool Downed { get; }

        /// <summary>Returns a copy of this sample carrying an explicit <paramref name="facingYaw"/> (world radians about
        /// +Y, 0 faces +Z), preserving every other field. The orthogonal way to add server-authoritative facing to ANY
        /// sample shape - position-only, exact-movement, or the fullest exact-speed sample - since facing is independent
        /// of the movement/speed/swim data. <see cref="CharacterAnimatorTuning.FacingYawOffset"/> still composes on top.</summary>
        public CharacterSample WithFacingYaw(float facingYaw) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, ClimbRate, HasPlanarSpeed, PlanarSpeed, facingYaw, StepCumulativeY, Downed);

        /// <summary>Returns a copy of this sample with <see cref="Downed"/> set to <paramref name="downed"/>, preserving
        /// every other field. The orthogonal way to mark ANY sample shape downed - position-only, exact-movement, the
        /// fullest exact-speed sample, or one already carrying an explicit facing - since the downed flag is independent
        /// of the movement/speed/swim/facing data. Mirrors <see cref="WithFacingYaw"/>. A networked game derives the
        /// argument from its own replicated state (e.g. <c>e.Hp &lt;= 0</c>) in the same per-frame loop that builds the
        /// sample.</summary>
        public CharacterSample WithDowned(bool downed) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, ClimbRate, HasPlanarSpeed, PlanarSpeed, FacingYaw, StepCumulativeY, downed);
    }

    /// <summary>A draw-ready character produced by <see cref="ReplicatedCharacterAnimators.Update"/>: the world
    /// transform + the bone palette to hand to <c>Scene3D.DrawSkinned(meshHandle, pose.Pose, pose.World, tint)</c>.
    /// The <see cref="Pose"/> buffer is the brain's own array, reused each frame, so a <see cref="CharacterPose"/> is
    /// valid only until the next <see cref="ReplicatedCharacterAnimators.Update"/>; draw it this frame, do not
    /// retain it.</summary>
    public readonly struct CharacterPose
    {
        public CharacterPose(long id, Matrix4x4 world, float glideFeetY, Matrix4x4[] pose, LocomotionState state, bool isLocal)
        {
            Id = id;
            World = world;
            _glideFeetY = glideFeetY;
            Pose = pose;
            State = state;
            IsLocal = isLocal;
        }

        // The stair-glide-smoothed feet-Y WITHOUT the discrete-step MESH offset (SmoothedY, not drawnFeetY). The DRAW
        // (World / RenderPosition) carries the step-event mesh offset so an isolated riser eases the drawn model. The
        // CAMERA must not inherit that mesh-only smoothing (it would dip the look-at on every curb), so CameraTarget is
        // built off this glide height instead. Equal to the drawn feet-Y whenever no step offset is active.
        readonly float _glideFeetY;

        /// <summary>The entity key this pose belongs to (matches <see cref="CharacterSample.Id"/>).</summary>
        public long Id { get; }

        /// <summary>The world transform: <c>scale * RotationY(facingYaw) * Translation(renderPosition)</c>. The uniform
        /// scale is <see cref="CharacterAnimatorTuning.Scale"/> (default 1), so the consumer can draw with this
        /// matrix directly. The facing yaw assumes the asset's rest pose faces +Z; see
        /// <see cref="CharacterAnimatorTuning.FacingYawOffset"/> for assets that do not. The translation is the SMOOTHED
        /// render position (see <see cref="RenderPosition"/>): the sample X/Z with the slope-glide-smoothed feet-Y, so the
        /// drawn model glides up stairs instead of bobbing per riser.</summary>
        public Matrix4x4 World { get; }

        /// <summary>The presentation position the character is DRAWN at this frame: the sample's X/Z (never smoothed, so
        /// movement stays responsive) with the feet-Y smoothed by the slope-fed stair-glide smoother
        /// (<see cref="CharacterAnimatorTuning.SlopeGlideRate"/>). This is the DRAW anchor - it sits at whatever the
        /// sample carried, which for a feet-anchored sample (the standard: <c>feet = centre - capsuleHalfHeight</c>) is
        /// the FEET. The drawn model already uses it via <see cref="World"/>. On flat ground and while airborne this
        /// equals the raw sample position (the smoother is identity there). Equal to <c>World.Translation</c> by
        /// construction (the smoothed translation is baked into <see cref="World"/>).
        /// <para><b>Do NOT point a follow camera at this directly when the sample is feet-anchored</b> - it drops the
        /// look-at a full capsule half-height below the character (the camera sits at the feet / floor). Use
        /// <see cref="CameraTarget"/> instead, which lifts the glide height back to the capsule centre.</para></summary>
        public Vector3 RenderPosition => World.Translation;

        /// <summary>The point to aim a third-person follow camera at: the stair-glide-smoothed feet height lifted by
        /// <paramref name="capsuleHalfHeight"/> so the look-at sits at the character's CENTRE, not the feet. The bridge
        /// is fed feet-anchored samples (<c>feet = centre - capsuleHalfHeight</c>) so the mesh draws with its feet on the
        /// ground via <see cref="World"/>. <see cref="RenderPosition"/> therefore sits at the feet, which is a full
        /// half-height too low to frame the character. Adding the half-height back reconstructs the smoothed centre - the
        /// same anchor a raw-physics follow camera targets (e.g. <c>WorldClient.LocalRenderState.Position</c>, the capsule
        /// centre) - while keeping the stair GLIDE (so the camera rises/falls smoothly on stairs instead of jolting per
        /// riser). Pass the same half-height used to build the sample.
        /// <para>Unlike <see cref="RenderPosition"/>, this uses the glide height WITHOUT the discrete-step MESH offset
        /// (<see cref="CharacterAnimatorTuning.StepSmoothingRate"/>): that step-event ease is a draw-only smoothing that
        /// keeps the MODEL from popping on an isolated riser, and letting it move the camera would dip the look-at on
        /// every curb/doorstep. So the camera tracks the continuous centre-glide and the mesh alone carries the step
        /// ease. On flat ground and airborne this is exactly the capsule centre (glide + step offset are both identity
        /// there), so a consumer can target it unconditionally.</para></summary>
        public Vector3 CameraTarget(float capsuleHalfHeight) =>
            new(World.Translation.X, _glideFeetY + capsuleHalfHeight, World.Translation.Z);

        /// <summary>Joint-WORLD bone palette for <c>Scene3D.DrawSkinned</c> (a <c>Matrix4x4[]</c>, so it passes
        /// straight to the span-taking draw call - same type as <see cref="AnimatedCharacter.Pose"/>). Transient (see
        /// the type remarks).</summary>
        public Matrix4x4[] Pose { get; }

        /// <summary>The locomotion state chosen this frame (handy for debug overlays).</summary>
        public LocomotionState State { get; }

        /// <summary>True for the local player (forwarded from the sample).</summary>
        public bool IsLocal { get; }
    }

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
                SwimClipSpeed)
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

    /// <summary>Owns one <see cref="AnimatedCharacter"/> per replicated entity and turns a per-frame stream of
    /// <see cref="CharacterSample"/>s into draw-ready <see cref="CharacterPose"/>s. The reusable bridge between
    /// "the netcode hands me positions" and "drive an animated avatar per player" - for the local player AND every
    /// remote, since position-over-time is the one signal every netcode surfaces for every entity.
    ///
    /// Per <see cref="Update"/>: a new id is created via the factory; a tracked id absent from the samples is dropped
    /// (no leak on disconnect); planar speed / vertical velocity / facing are derived from the position displacement
    /// averaged over a short window (so a plateauing / zero-delta position stream does not strobe the state; the
    /// exact grounded flag + vertical velocity are used instead when the sample <see cref="CharacterSample.HasMovement"/>,
    /// and the facing is taken from the sample's explicit <see cref="CharacterSample.FacingYaw"/> when supplied - which
    /// turns the character in place at rest and overrides the derived heading while moving);
    /// the swim flag is exact-only (<see cref="CharacterSample.Swimming"/>, the replicated <c>MovementState.Swimming</c>
    /// bit) since a swimmer glides horizontally like a walker and cannot be told from one by position;
    /// the locomotion state machine inside <see cref="AnimatedCharacter"/> picks the clip. The set owns no GPU handle
    /// and never calls <c>Scene3D</c> - iterate <see cref="Live"/> and draw - so it is fully headless-testable.
    /// Client-cosmetic: never feed a pose back into simulation or netcode.</summary>
    public sealed class ReplicatedCharacterAnimators
    {
        // The active pose OVERRIDE for an entity: a whole-body pose that replaces locomotion selection for as long as
        // it is set. None is normal locomotion. Downed (death / knockdown) is the only override today. The seam is
        // deliberately an enum, not a bool, so a future non-locomotion pose (Stunned, Sitting, an emote, a mount ride)
        // slots in as a new value with its own entry/hold logic without reworking the None-vs-override branch in Update.
        enum PoseOverride { None, Downed }

        sealed class Entry
        {
            public AnimatedCharacter Character = null!;
            public Vector3 PrevPosition;
            public bool HasPrev;
            public float Yaw;
            public Vector3 DispAccum;   // displacement summed within the current velocity window
            public float TimeAccum;     // elapsed time summed within the current velocity window
            public Vector3 Velocity;    // last closed-window velocity, held across zero-delta frames
            public float SmoothedY;     // signal-gated render-glide feet height (see the smoother in Update); seeded to true
            public bool SnapPending;    // a consumer called SnapRenderHeight: hard-cut the render height next Update
            public bool AscendGliding;  // the ASCENT climb feed-forward (or its disengage ease) was active last Update.
                                        // Gates the disengage ease to an ascent crest ONLY: a fall never sets it (falls
                                        // render raw), and a DESCENT sets it false (ClimbRate < 0), so the descent's
                                        // ClimbRate==0 flicker ticks hard-cut and track the drop instead of easing.
            public float StepOffset;    // DISCRETE-STEP mesh offset (metres, SUBTRACTED from the drawn feet): the UE-style
                                        // step-event smoother's decaying vertical offset that eases an isolated step the
                                        // continuous glide rendered raw. Positive = mesh drawn BELOW the true feet (a
                                        // step-up, easing up); negative = above (a step-down, easing down). Decays to 0.
                                        // Re-anchored (frozen) on a detected step so the mesh holds at its previous drawn
                                        // height and eases, never overshooting past the pre-step (see the smoother).
            public float LastStepCumulative;   // last CharacterSample.StepCumulativeY consumed: the step-detect baseline.
                                        // Seeded to the first sample (no session-history dump) and re-synced on a teleport.
            public float PrevDrawnY;    // the previous frame's DRAWN feet-Y: the height the step freeze holds the mesh at.
            public PoseOverride Override;   // the active whole-body pose override (None = normal locomotion). Set on the
                                        // rising edge of CharacterSample.Downed, cleared on the falling edge.
            public float OverrideElapsed;   // seconds the current Override has been active. Drives the procedural collapse
                                        // ramp (0 -> DownedCollapseSeconds). 0 while Override == None.
        }

        // The disengage ease (climb -> grounded-flat) snaps exact and ends once the residual falls below this: 1 mm is
        // sub-perceptual (well under a millimetre per frame at the settle tail), so the ease terminates cleanly rather
        // than chasing an asymptote onto flat ground.
        const float SettleEpsilon = 0.001f;

        readonly Func<AnimatedCharacter> _factory;
        readonly CharacterAnimatorTuning _tuning;
        readonly Dictionary<long, Entry> _entries = new();
        readonly List<CharacterPose> _live = new();
        readonly HashSet<long> _seen = new();
        readonly List<long> _toRemove = new();

        /// <summary>Build the set from a factory that fully constructs a brain (skeleton + clips + its own
        /// thresholds/crossfade). <see cref="CharacterAnimatorTuning.Locomotion"/> / <see cref="CharacterAnimatorTuning.Crossfade"/>
        /// are NOT applied here (the factory owns them); the other tuning fields still govern the derivation.</summary>
        public ReplicatedCharacterAnimators(Func<AnimatedCharacter> factory, CharacterAnimatorTuning? tuning = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _tuning = tuning ?? CharacterAnimatorTuning.Default;
        }

        /// <summary>Convenience: build one brain per entity off a shared (immutable) skeleton + clip map, applying
        /// <see cref="CharacterAnimatorTuning.Locomotion"/> + <see cref="CharacterAnimatorTuning.Crossfade"/>. The
        /// skeleton/clips are safe to share - each brain keeps its own playhead.</summary>
        public ReplicatedCharacterAnimators(Skeleton skeleton,
            IReadOnlyDictionary<LocomotionState, AnimationClip> clips, CharacterAnimatorTuning? tuning = null)
            : this(BuildFactory(skeleton, clips, tuning ?? CharacterAnimatorTuning.Default), tuning)
        {
        }

        static Func<AnimatedCharacter> BuildFactory(Skeleton skeleton,
            IReadOnlyDictionary<LocomotionState, AnimationClip> clips, CharacterAnimatorTuning tuning)
        {
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            if (clips is null) throw new ArgumentNullException(nameof(clips));
            LocomotionSpeedSync speedSync = tuning.SpeedSync();
            return () => new AnimatedCharacter(skeleton, clips, tuning.Locomotion, tuning.Crossfade, tuning.StateDebounceSeconds, speedSync);
        }

        /// <summary>The live characters this frame, in sample order. Iterate and draw each with
        /// <c>Scene3D.DrawSkinned(meshHandle, pose.Pose, pose.World, tint)</c>. Rebuilt every <see cref="Update"/>.</summary>
        public IReadOnlyList<CharacterPose> Live => _live;

        /// <summary>The <see cref="AnimatedCharacter"/> brain the set owns for entity <paramref name="id"/>, or null if
        /// no entity with that id is tracked (it has not been sampled yet, or was dropped on disconnect). This is how a
        /// game plays a one-shot ACTION on a REPLICATED remote: when it receives the action trigger as a game message,
        /// it looks up the remote's brain here and calls <see cref="AnimatedCharacter.PlayAction"/> on it (the local
        /// animator API is callable for remotes too - it holds no ownership/authority state). Replicating the trigger
        /// itself is a game-message concern, out of scope for this bridge. Client-cosmetic: never feed a pose back into
        /// simulation or netcode.</summary>
        public AnimatedCharacter? BrainFor(long id) => _entries.TryGetValue(id, out Entry? e) ? e.Character : null;

        /// <summary>Hard-cut the render height for entity <paramref name="id"/> on its NEXT <see cref="Update"/>: the
        /// render-height glide snaps the drawn feet-Y straight to the true feet-Y and renders raw THAT frame (even if the
        /// sample carries a climb signal), restarting the velocity window from the destination instead of gliding. No-op
        /// if <paramref name="id"/> is not tracked (it has not been sampled yet, or was dropped on disconnect).
        ///
        /// <para>This is the consumer hook for an AUTHORITATIVE TELEPORT (a teleport-epoch advance: admin move,
        /// self-rescue, fast-travel, respawn). The smoother's built-in gap snap only guarantees a hard cut when the
        /// vertical jump exceeds <see cref="CharacterAnimatorTuning.SlopeGlideSnapDistance"/> (1.5 m); a SHORT teleport
        /// under that distance is indistinguishable from a stair riser by height alone and would otherwise glide - no
        /// height heuristic can tell the two apart, so the consumer's teleport signal is the only reliable source.
        /// Wire it to the teleport signal the netcode already raises: for the LOCAL player call it when
        /// <c>WorldClient.LocalTeleportEpoch</c> advances (or from the <c>WorldClient.LocalTeleported</c> event); for
        /// REMOTES call it for each id in <c>WorldClient.RemoteTeleports</c> right after <c>WorldClient.Poll</c>. With
        /// that wiring EVERY teleport is an exact hard cut at any gap size; without it, only gaps above the snap
        /// distance cut. Call it any time before the next <see cref="Update"/> (order-independent - it defers the snap
        /// to that Update, so whether the destination position has been sampled yet does not matter).</para></summary>
        public void SnapRenderHeight(long id)
        {
            if (_entries.TryGetValue(id, out Entry? e)) e.SnapPending = true;
        }

        /// <summary>Advance every tracked character one frame from this frame's samples. Call once per render frame. An
        /// entity whose sample sets <see cref="CharacterSample.Downed"/> takes the downed pose override (locomotion
        /// suppressed; the baked <see cref="LocomotionState.Downed"/> clip held on its final frame, or a procedural
        /// collapse to prone) instead of the locomotion path.</summary>
        public void Update(IReadOnlyList<CharacterSample> samples, float dt)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            _live.Clear();
            _seen.Clear();

            for (int i = 0; i < samples.Count; i++)
            {
                CharacterSample s = samples[i];
                _seen.Add(s.Id);

                if (!_entries.TryGetValue(s.Id, out Entry? e))
                {
                    e = new Entry
                    {
                        Character = _factory() ?? throw new InvalidOperationException("the AnimatedCharacter factory returned null."),
                        PrevPosition = s.Position,
                        HasPrev = false,
                        // Seed the first-observation yaw from an explicit server-authoritative facing when the sample
                        // supplies one, so a server-faced entity SPAWNS already facing correctly instead of turning in
                        // from the default yaw 0 over several frames. The seed matches the facing target below
                        // (FacingYaw + FacingYawOffset), so the first frame's LerpAngle has zero delta and holds it.
                        // No explicit facing -> default 0 (the derived path turns in from travel as before).
                        Yaw = s.FacingYaw.HasValue ? s.FacingYaw.Value + _tuning.FacingYawOffset : 0f,
                        // Seed the smoothed feet-Y at the true height so a spawn draws exactly at the sample position (no
                        // ease-in from 0), and so flat ground stays byte-identical (the damp-toward-true is a no-op from
                        // an already-equal state).
                        SmoothedY = s.Position.Y,
                        // Seed the discrete-step diff baseline at the first sample's cumulative so a spawn does NOT dump the
                        // whole session's accumulated step sum into the mesh offset (StepOffset defaults 0 - no ease-in),
                        // and seed the freeze reference at the spawn feet so the first frame is identity.
                        LastStepCumulative = s.StepCumulativeY,
                        PrevDrawnY = s.Position.Y,
                    };
                    _entries[s.Id] = e;
                }

                // A consumer signalled an authoritative teleport for this id (SnapRenderHeight, wired to the netcode
                // teleport epoch): hard-cut the render height to the destination and restart the derivation from it, so
                // a SHORT blink under SlopeGlideSnapDistance cuts crisply instead of gliding (no height heuristic can
                // tell a short teleport from a stair riser - the consumer's signal is the only reliable source). Treat
                // the destination exactly like a fresh observation: seed SmoothedY at the true feet-Y, drop the stale
                // velocity window, and clear HasPrev so this frame derives no motion from the teleport delta. The
                // per-frame `snapped` flag makes the smoother render raw THIS frame even if the sample carries a climb
                // signal (a teleport is a clean cut, never a glide). Cleared here; applies to exactly this one Update.
                bool snapped = false;
                if (e.SnapPending)
                {
                    e.SnapPending = false;
                    snapped = true;
                    e.PrevPosition = s.Position;
                    e.HasPrev = false;
                    e.SmoothedY = s.Position.Y;
                    e.DispAccum = Vector3.Zero;
                    e.TimeAccum = 0f;
                    e.Velocity = Vector3.Zero;
                    // Zero the discrete-step mesh offset, re-sync its diff baseline to the destination cumulative, and reset
                    // the freeze reference to the destination feet, so a teleport is an exact hard cut: the cumulative reset
                    // a teleport/reconnect carries (Reset/Reseed zero ClientPrediction.StepCumulativeY) is absorbed here,
                    // never read as a spurious step next frame.
                    e.StepOffset = 0f;
                    e.LastStepCumulative = s.StepCumulativeY;
                    e.PrevDrawnY = s.Position.Y;
                }

                // POSE OVERRIDE (downed / death). A whole-body pose that REPLACES locomotion for as long as the game
                // marks the entity CharacterSample.Downed (derived client-side from replicated state - the engine knows
                // nothing about HP or death rules). Detect the edges and drive the per-entity override state machine.
                PoseOverride requested = s.Downed ? PoseOverride.Downed : PoseOverride.None;
                if (requested != e.Override)
                {
                    e.Override = requested;
                    e.OverrideElapsed = 0f;
                    if (requested == PoseOverride.Downed) e.Character.EnterDowned();
                    else e.Character.ExitDowned();   // falling edge (respawn / revive): back to normal locomotion
                }

                if (e.Override == PoseOverride.Downed)
                {
                    // DOWNED: locomotion (idle/walk/run, air, swim) AND stacked action one-shots are suppressed for this
                    // entity. The locomotion Update is not called. UpdateDowned holds the death-clip final frame (clip
                    // rig) or freezes the neutral pose (procedural rig). The yaw is FROZEN at whatever the entity faced
                    // when it went down (a corpse does not turn), so no facing derivation runs here.
                    e.OverrideElapsed += dt > 0f ? dt : 0f;
                    e.Character.UpdateDowned(dt);

                    // Settle at ground level: the render height is the TRUE feet-Y with no stair-glide / step-offset
                    // carryover, and the glide/step smoother state is reset so a later respawn (which also snaps via
                    // SnapRenderHeight) resumes cleanly. Freeze the velocity window too - the derived speed must read 0
                    // when locomotion resumes, not a stale pre-death velocity.
                    float feetY = s.Position.Y;
                    e.SmoothedY = feetY;
                    e.AscendGliding = false;
                    e.StepOffset = 0f;
                    e.LastStepCumulative = s.StepCumulativeY;
                    e.PrevDrawnY = feetY;
                    e.DispAccum = Vector3.Zero;
                    e.TimeAccum = 0f;
                    e.Velocity = Vector3.Zero;

                    Matrix4x4 downedWorld;
                    if (e.Character.HasDownedClip)
                    {
                        // The Downed clip lays the body down in SKELETON space, so the world stays upright (scale +
                        // frozen yaw + feet at ground). UpdateDowned holds the clip's final frame.
                        downedWorld = Matrix4x4.CreateScale(_tuning.Scale)
                                      * Matrix4x4.CreateRotationY(e.Yaw)
                                      * Matrix4x4.CreateTranslation(s.Position.X, feetY, s.Position.Z);
                    }
                    else
                    {
                        // No clip: PROCEDURAL collapse. Tip the (frozen neutral) body from upright to prone over
                        // DownedCollapseSeconds via a smoothstep ramp. The tip rotates about the model's LOCAL lateral
                        // axis (RotationX BEFORE the yaw in the multiply chain), so the body topples FORWARD in its
                        // facing direction. The yaw then carries that lie direction to the world facing. Pivoting at the
                        // feet origin (the sample is feet-anchored) swings the whole body down onto the ground plane, so
                        // it lies flat at ground level rather than floating at capsule centre. At full tip (pi/2) the
                        // model's up axis is horizontal - a body on the floor, not a leaning statue.
                        float collapse = _tuning.DownedCollapseSeconds > 0f
                            ? Math.Clamp(e.OverrideElapsed / _tuning.DownedCollapseSeconds, 0f, 1f)
                            : 1f;
                        float eased = collapse * collapse * (3f - 2f * collapse);   // smoothstep (monotonic 0->1)
                        float tip = eased * (MathF.PI / 2f);
                        downedWorld = Matrix4x4.CreateScale(_tuning.Scale)
                                      * Matrix4x4.CreateRotationX(tip)
                                      * Matrix4x4.CreateRotationY(e.Yaw)
                                      * Matrix4x4.CreateTranslation(s.Position.X, feetY, s.Position.Z);
                    }

                    _live.Add(new CharacterPose(s.Id, downedWorld, feetY, e.Character.Pose, e.Character.State, s.IsLocal));
                    e.PrevPosition = s.Position;
                    e.HasPrev = true;
                    continue;   // skip the locomotion path entirely while downed
                }

                // Derive velocity over a short time WINDOW, not a single frame. The rendered position PLATEAUS
                // between server ticks - ClientPrediction.RenderedState clamps the inter-tick fraction at 1, so once
                // interpolation saturates the position is constant until the next Predict - which means render fps >
                // tick rate yields one or more ZERO-DELTA frames per tick. A single-frame derivation reads speed 0 on
                // those frames and strobes the locomotion state Idle<->moving every frame (and AnimationPlayer.Play
                // restarts the clip on every state change, freezing the animation). Averaging displacement over ~1
                // tick and HOLDING the last good velocity between window closes keeps the speed steady across the
                // plateau. The first frame for an id (or a non-positive dt) has no usable delta -> velocity stays
                // zero (Idle), never NaN. window <= 0 reverts to per-frame derivation (closes every frame).
                if (e.HasPrev && dt > 0f)
                {
                    e.DispAccum += s.Position - e.PrevPosition;
                    e.TimeAccum += dt;
                    if (e.TimeAccum >= _tuning.VelocityWindowSeconds)
                    {
                        e.Velocity = e.DispAccum / e.TimeAccum;
                        e.DispAccum = Vector3.Zero;
                        e.TimeAccum = 0f;
                    }
                }

                Vector3 planarVel = new Vector3(e.Velocity.X, 0f, e.Velocity.Z);
                float derivedVertical = e.Velocity.Y;
                float derivedPlanarSpeed = planarVel.Length();

                // Exact movement (local player) wins over the derived signals when present.
                float verticalVelocity = s.HasMovement ? s.VerticalVelocity : derivedVertical;
                bool grounded = s.HasMovement
                    ? s.Grounded
                    : MathF.Abs(verticalVelocity) < _tuning.GroundedVerticalEpsilon;
                // Locomotion state + clip-speed sync run off the exact planar speed when supplied (the clean commanded
                // speed), so a decel-to-stop does not strobe walk<->idle off the finite-differenced render position.
                // Facing still takes its DIRECTION from the derived heading (exact speed is magnitude-only), but gates
                // on the exact speed too (see below) so it holds through the post-stop settle instead of spinning.
                float locomotionSpeed = s.HasPlanarSpeed ? MathF.Max(0f, s.PlanarSpeed) : derivedPlanarSpeed;

                // Swim is an EXACT flag only (the replicated MovementState.Swimming bit): it cannot be derived from
                // position because a swimmer glides horizontally like a walker. A position-only sample never swims.
                bool swimming = s.HasMovement && s.Swimming;

                // Facing has two sources. EXPLICIT server-authoritative facing (CharacterSample.FacingYaw) WINS when the
                // sample supplies it: the yaw target is the supplied facing plus the asset offset, run through the SAME
                // LerpAngle smoothing (so the turn rate and the +/-pi wrap are shared with the derived path). It applies
                // whether or not the entity is moving - server authority beats the position-derived heading - and it
                // turns a STATIONARY entity in place (the derived path below holds the yaw at rest; explicit facing does
                // not). That is the whole point of the seam: a server-owned NPC standing at melee range, a turret, a
                // mount, or a player turning on the spot can face where the server says even though a position delta
                // reveals nothing at rest.
                //
                // DERIVED facing (no explicit value) is unchanged: aim along the derived planar heading, but only while
                // the entity is genuinely moving. The derived heading (from the render-position delta) swings around
                // during the post-stop render settle - the local avatar's rendered position sags backward then recovers,
                // so the delta briefly points backward/sideways - and chasing it spins the model for a few frames before
                // it corrects. So gate on the EXACT planar speed too when it is supplied (the local player): at a real
                // stop it is 0, holding the yaw through the settle. Remotes (no exact speed) gate on the derived speed
                // alone as before. The derived magnitude is still required so there is a valid heading direction for the
                // Atan2. Below the threshold the yaw holds (no spin at rest).
                if (s.FacingYaw.HasValue)
                {
                    float target = s.FacingYaw.Value + _tuning.FacingYawOffset;
                    e.Yaw = LerpAngle(e.Yaw, target, _tuning.YawSmoothing);
                }
                else
                {
                    bool movingForFacing = derivedPlanarSpeed > _tuning.MinPlanarSpeedForFacing
                        && (!s.HasPlanarSpeed || locomotionSpeed > _tuning.MinPlanarSpeedForFacing);
                    if (movingForFacing)
                    {
                        float target = MathF.Atan2(planarVel.X, planarVel.Z) + _tuning.FacingYawOffset;
                        e.Yaw = LerpAngle(e.Yaw, target, _tuning.YawSmoothing);
                    }
                }

                e.Character.Update(locomotionSpeed, grounded, verticalVelocity, swimming, dt);

                // SIGNAL-GATED render-height glide: turn the paced stair-climb sim's per-riser vertical bob into a smooth
                // glide up the stair slope, for the drawn model (baked into World below) AND a follow camera
                // (CharacterPose.RenderPosition), driven ENTIRELY by the sim's exported climb rate (CharacterSample.ClimbRate)
                // - never estimated from position deltas. The estimator (grade windows, clamps, the ballistic threshold,
                // the horizontal-motion gate) is gone: the sim already knows when it is climbing and how fast, so the
                // glide is correct BY CONSTRUCTION. A fall, jump, teleport, prop platform, elevator, or moving platform is
                // never stamped with a climb rate (ClimbRate == 0), so it takes the raw branch - render-Y is the true
                // feet-Y, no glide, nothing to carry past the floor at touchdown. THAT is why the 1.2 m fall-sink cannot
                // recur: a fall's ClimbRate is 0, so the smoother never engages during a fall. Flat ground is
                // byte-identical (ClimbRate == 0 -> raw -> render-Y == true feet-Y exactly, from the seeded state).
                float trueFeetY = s.Position.Y;
                bool climbing = s.ClimbRate != 0f;   // the sim's fact: 0 = not on a step climb (position-only samples read 0)
                float glideStep = 1f - MathF.Exp(-_tuning.SlopeGlideRate * dt);
                if (_tuning.SlopeGlideRate <= 0f || dt <= 0f || snapped
                    || MathF.Abs(trueFeetY - e.SmoothedY) > _tuning.SlopeGlideSnapDistance)
                {
                    // Disabled / a teleport cut this frame / a gap larger than the snap distance: render raw (hard cut).
                    e.SmoothedY = trueFeetY;
                    e.AscendGliding = false;
                }
                else if (climbing)
                {
                    // Lag-free feed-forward at the EXACT sim rate (signed: ascent raises, descent lowers), then critically
                    // damp toward the true feet-Y. The ascent ClimbRate is now the EWMA of the ACHIEVED per-tick rise
                    // (CharacterMovement step 4b), so it converges to the true climb rate and this feed-forward/damp
                    // equilibrium sits ON the true feet (~0 hover) instead of a half-riser above - no persistent stair
                    // float, and no hover left to snap when the signal cuts to 0 at the top.
                    e.SmoothedY += s.ClimbRate * dt;
                    e.SmoothedY += (trueFeetY - e.SmoothedY) * glideStep;
                    e.AscendGliding = s.ClimbRate > 0f;   // ascent arms the crest ease; descent does not (see below)
                }
                else if (e.AscendGliding && grounded && locomotionSpeed > 0f)
                {
                    // DISENGAGE EASE (ASCENT crest -> grounded-flat while STILL MOVING). The signal just cut to 0 at the top
                    // of a climb, but the drawn feet can still carry the last per-riser hover (~1-2 cm at the disengage
                    // phase). Ease it onto the true feet with the SAME critical damp instead of hard-cutting that residual in
                    // a single frame - that one-frame drop is the crest snap. Tightly gated so nothing else changes:
                    //  - `AscendGliding` means an ASCENT was gliding last frame, so it is scoped to the ascent crest (the
                    //    only place the snap occurs). A DESCENT does NOT arm it (ClimbRate < 0), so the descent's
                    //    ClimbRate==0 flicker ticks (a full riser drop the sim reads as "not on a run" for a tick) hard-cut
                    //    and TRACK the drop, exactly as before - no descent regression.
                    //  - a FALL renders raw and never arms it, so it can never enter here even on its grounded landing tick;
                    //    the fall-sink stays impossible by construction.
                    //  - a mid-stair STOP (locomotionSpeed 0) hard-cuts, so the feet sit on the true tread immediately (no
                    //    post-stop float).
                    // Once the residual eases below SettleEpsilon, snap exact and disarm, so it cannot leave a sub-perceptual
                    // offset running onto flat ground (and genuinely flat ground never climbs, so it never arms - flat-ground
                    // identity holds).
                    e.SmoothedY += (trueFeetY - e.SmoothedY) * glideStep;
                    if (MathF.Abs(trueFeetY - e.SmoothedY) <= SettleEpsilon) { e.SmoothedY = trueFeetY; e.AscendGliding = false; }
                }
                else
                {
                    // Not climbing, and either stopped, airborne, descending-flicker, or already settled: render raw (hard
                    // cut). Correct by construction for a fall, jump, teleport, prop platform, elevator, swim, mid-stair
                    // stop, or a descent's between-riser tick.
                    e.SmoothedY = trueFeetY;
                    e.AscendGliding = false;
                }

                // DISCRETE-STEP mesh offset (UE-style step-event smoothing): ease an ISOLATED step the continuous glide
                // above rendered raw (its signal ClimbRate == 0, so SmoothedY just tracked the true feet and would pop the
                // step). The sim exports each committed isolated-step impulse as a session-monotonic running sum
                // (CharacterSample.StepCumulativeY, from the local predictor); DIFF it to DETECT each new step EXACTLY ONCE
                // (the predictor increments it only on the Predict boundary, never on a reconcile replay - the diff inherits
                // that exactly-once). On a detected step, FREEZE the mesh at its previous drawn height (re-anchor the offset
                // to SmoothedY - PrevDrawnY) and then decay that offset to 0 in render time (offset *= e^(-rate*dt),
                // frame-rate independent), so the mesh holds where it was and eases to the new true feet.
                //
                // Why FREEZE, not accumulate the raw impulse: the SIM commits the step at a tick boundary (the cumulative
                // jumps fully), but the sample feet-Y is the INTER-TICK-INTERPOLATED render position, which is only PART
                // way through the step on the frames right after the commit. Adding the full impulse to that mid-interp
                // height OVERSHOOTS - a step-up sinks the mesh BELOW the pre-step floor, a step-down bumps it ABOVE the
                // pre-step - a reversal that reads worse than the pop. Freezing at the last drawn height absorbs that
                // interp/commit phase mismatch exactly (the bridge has no inter-tick fraction to interpolate the offset
                // with), so the mesh never crosses the pre-step height: it stays between the pre-step and the true feet
                // and eases monotonically. Composes with the glide by construction (the sim stamps EITHER a ClimbRate OR a
                // step impulse per tick, never both), so a continuous run leaves the cumulative unchanged (the offset just
                // decays) and a run's first-riser offset decays out as the glide takes over. Inert for remotes /
                // position-only samples (StepCumulativeY stays 0, so no step is ever detected). A cumulative jump beyond the
                // snap distance is a teleport re-baseline that slipped the SnapPending re-sync, not a step -> hard-cut. On a
                // `snapped` frame the SnapPending block above already zeroed the offset and re-synced the baseline + freeze
                // reference, so no step is detected and it renders raw.
                //
                // Two edge paths were TRACED SOUND but are NOT yet pinned by a test (candidates for future coverage):
                // (1) a DIVERGENT-BASIS reconcile - the cumulative is authored on the commanded (predict) path and rides a
                //     reconcile rebase onto a divergent server basis UNCHANGED, so this diff stays exactly-once even when the
                //     authoritative basis jumps under the replay; and
                // (2) TWO STEPS IN ONE render window - if two impulses commit between frames the diff sees their SUM and
                //     freezes once at the pre-step height (correct: the mesh still eases the combined rise monotonically, it
                //     just does not resolve them as two separate eases).
                float stepDelta = s.StepCumulativeY - e.LastStepCumulative;
                e.LastStepCumulative = s.StepCumulativeY;
                float drawnFeetY = e.SmoothedY;
                if (_tuning.StepSmoothingRate > 0f)
                {
                    if (dt > 0f) e.StepOffset *= MathF.Exp(-_tuning.StepSmoothingRate * dt);   // age the ease toward 0
                    if (stepDelta != 0f)
                    {
                        // A new discrete step: re-anchor so the mesh holds at its previous drawn height (freeze), UNLESS the
                        // jump is too large to be a real step (a teleport re-baseline) - then hard-cut instead.
                        e.StepOffset = MathF.Abs(stepDelta) <= _tuning.SlopeGlideSnapDistance ? e.SmoothedY - e.PrevDrawnY : 0f;
                    }
                    e.StepOffset = Math.Clamp(e.StepOffset, -_tuning.SlopeGlideSnapDistance, _tuning.SlopeGlideSnapDistance);
                    drawnFeetY = e.SmoothedY - e.StepOffset;
                }
                else e.StepOffset = 0f;
                e.PrevDrawnY = drawnFeetY;

                Matrix4x4 world = Matrix4x4.CreateScale(_tuning.Scale)
                                  * Matrix4x4.CreateRotationY(e.Yaw)
                                  * Matrix4x4.CreateTranslation(s.Position.X, drawnFeetY, s.Position.Z);
                // Draw feet (drawnFeetY) carry the step-event mesh offset. The camera anchor uses the glide height
                // (SmoothedY) alone so an isolated riser's ease never dips the follow camera (see CharacterPose.CameraTarget).
                _live.Add(new CharacterPose(s.Id, world, e.SmoothedY, e.Character.Pose, e.Character.State, s.IsLocal));

                e.PrevPosition = s.Position;
                e.HasPrev = true;
            }

            // Drop brains for ids no longer present (no leak on disconnect).
            if (_entries.Count != _seen.Count)
            {
                _toRemove.Clear();
                foreach (long id in _entries.Keys)
                    if (!_seen.Contains(id)) _toRemove.Add(id);
                for (int i = 0; i < _toRemove.Count; i++) _entries.Remove(_toRemove[i]);
            }
        }

        // Shortest-path angle lerp: step the stored yaw toward the target by t (per-frame factor, clamped 0..1).
        static float LerpAngle(float current, float target, float t)
        {
            float delta = WrapPi(target - current);
            return current + delta * Math.Clamp(t, 0f, 1f);
        }

        // Wrap an angle into (-pi, pi].
        static float WrapPi(float a)
        {
            const float twoPi = MathF.PI * 2f;
            a %= twoPi;
            if (a > MathF.PI) a -= twoPi;
            else if (a < -MathF.PI) a += twoPi;
            return a;
        }
    }
}
