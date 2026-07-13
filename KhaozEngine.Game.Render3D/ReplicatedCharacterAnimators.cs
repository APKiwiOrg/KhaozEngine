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
        // Full-field constructor (private) backing WithFacingYaw: copies every field and overrides the facing yaw.
        // Keeping it private avoids a public 10-arg overload; the public constructors below stay the documented surface.
        CharacterSample(long id, Vector3 position, bool isLocal, bool hasMovement, bool grounded, float verticalVelocity,
            bool swimming, bool hasPlanarSpeed, float planarSpeed, float? facingYaw)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = hasMovement;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            HasPlanarSpeed = hasPlanarSpeed;
            PlanarSpeed = planarSpeed;
            FacingYaw = facingYaw;
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
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
        }

        /// <summary>Sample with exact movement (the local player, or any entity whose replicated <c>MovementState</c>
        /// is available): <see cref="Grounded"/>, <see cref="VerticalVelocity"/>, and <see cref="Swimming"/> are used
        /// as given instead of being derived. <paramref name="swimming"/> defaults false so a pre-swim caller is
        /// unchanged.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, bool swimming = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
        }

        /// <summary>Fullest sample (the local player): exact <see cref="Grounded"/>, <see cref="VerticalVelocity"/>,
        /// AND exact planar <paramref name="planarSpeed"/> (m/s). The planar speed drives the locomotion state and the
        /// clip-speed sync DIRECTLY instead of being finite-differenced from the rendered position. Pass the clean
        /// commanded speed (<c>WorldClient.LocalHorizontalSpeed</c> / <c>ClientPrediction.PredictedHorizontalSpeed</c>):
        /// it is computed only on the prediction's commanded path, so it does not carry the reconciliation render offset
        /// and does not strobe walk&lt;-&gt;idle when the player decelerates to a stop (where the rendered position, even
        /// after the C1 smoothing fix, settles with a tiny residual sag). Facing still follows the derived heading
        /// (planar speed is magnitude-only). A negative value is treated as zero.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, float planarSpeed, bool swimming = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            HasPlanarSpeed = true;
            PlanarSpeed = planarSpeed;
            FacingYaw = null;
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
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = facingYaw;
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

        /// <summary>Returns a copy of this sample carrying an explicit <paramref name="facingYaw"/> (world radians about
        /// +Y, 0 faces +Z), preserving every other field. The orthogonal way to add server-authoritative facing to ANY
        /// sample shape - position-only, exact-movement, or the fullest exact-speed sample - since facing is independent
        /// of the movement/speed/swim data. <see cref="CharacterAnimatorTuning.FacingYawOffset"/> still composes on top.</summary>
        public CharacterSample WithFacingYaw(float facingYaw) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, HasPlanarSpeed, PlanarSpeed, facingYaw);
    }

    /// <summary>A draw-ready character produced by <see cref="ReplicatedCharacterAnimators.Update"/>: the world
    /// transform + the bone palette to hand to <c>Scene3D.DrawSkinned(meshHandle, pose.Pose, pose.World, tint)</c>.
    /// The <see cref="Pose"/> buffer is the brain's own array, reused each frame, so a <see cref="CharacterPose"/> is
    /// valid only until the next <see cref="ReplicatedCharacterAnimators.Update"/>; draw it this frame, do not
    /// retain it.</summary>
    public readonly struct CharacterPose
    {
        public CharacterPose(long id, Matrix4x4 world, Matrix4x4[] pose, LocomotionState state, bool isLocal)
        {
            Id = id;
            World = world;
            Pose = pose;
            State = state;
            IsLocal = isLocal;
        }

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
        /// (<see cref="CharacterAnimatorTuning.SlopeGlideRate"/>). Point a follow camera's target at THIS (not the raw
        /// sample/predicted position) so the camera glides up and down stairs with the model instead of jolting on each
        /// riser; the drawn model already uses it via <see cref="World"/>. On flat ground and while airborne this equals
        /// the raw sample position (the smoother is identity there), so a consumer can target it unconditionally. Equal to
        /// <c>World.Translation</c> by construction (the smoothed translation is baked into <see cref="World"/>).</summary>
        public Vector3 RenderPosition => World.Translation;

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

        /// <summary>Critical-damp settle rate (radians/second) of the SLOPE-FED render-height smoother that makes a stair
        /// climb read as a smooth glide up the stair slope instead of a per-riser bob. The paced stair-climb sim
        /// deliberately produces a per-riser vertical sawtooth (a ~120-140 mm peak-to-peak render-Y bob at 4-9 Hz on a
        /// 0.30/0.40 staircase - the deliberate per-riser pause, unchanged), which a plain low-pass cannot flatten without
        /// lagging the feet on the ramp. Instead <see cref="ReplicatedCharacterAnimators.Update"/> advances a smoothed
        /// feet-Y by the windowed MEAN vertical climb RATE (<c>EWMA of dY/dt</c>, gated by the estimated grade from a
        /// short dY/dXZ window), which tracks the ramp line with NO lag but does not couple to the uneven per-tick
        /// horizontal (the co-paced ascent makes horizontal anticorrelate with the rise; the old horizontalDelta*grade
        /// form judder-injected worse than raw on a run-up). It then critically damps that smoothed-Y toward the true
        /// feet-Y at THIS rate to correct rate drift and settle onto real tread tops. The smoothed height is baked into
        /// <see cref="CharacterPose.World"/> and exposed as <see cref="CharacterPose.RenderPosition"/> (point a follow camera at that).
        /// <para>Default 5 (rad/s). Derivation: the per-riser sawtooth sits at 4-9 Hz (25-56 rad/s); a first-order response
        /// at 5 rad/s attenuates that band to about a fifth of the raw bob (the feed-forward carries the ramp, this damping
        /// barely responds to the fast bob) yet settles a mid-stair rest offset to a few mm in about 0.8 s. On flat ground
        /// the grade reads ~0, the feed-forward term is gated off, and the damp-toward-true is a no-op from the seeded state, so
        /// render-Y equals the true Y byte-for-byte (identity, no behaviour change). <b>Set &lt;= 0 to disable</b> the
        /// smoother entirely (render-Y is always the true feet-Y, byte-identical to the pre-feature bridge).</para></summary>
        public float SlopeGlideRate;

        /// <summary>Render-height gap (metres) beyond which the slope-fed smoother SNAPS the smoothed feet-Y to the true
        /// feet-Y instead of gliding - a fall, a jump takeoff, a ledge walk-off, or a LARGE teleport should be crisp, not
        /// crawl up over a fraction of a second. Mirrors <see cref="CharacterAvatar.RenderHeightSnapDistance"/>: default
        /// 1.5, well above any single stair riser (0.30) and below a floor-to-floor jump. A teleport whose vertical gap
        /// exceeds this snaps same-frame automatically; a SHORT teleport under it is height-identical to a stair riser,
        /// so the hard cut for those comes from <see cref="ReplicatedCharacterAnimators.SnapRenderHeight"/> (the consumer
        /// hook wired to the netcode teleport epoch), not this gap. Only consulted when
        /// <see cref="SlopeGlideRate"/> &gt; 0.</summary>
        public float SlopeGlideSnapDistance;

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
        };

        /// <summary>Default <see cref="SlopeGlideRate"/> (rad/s): 5. See that field for the derivation.</summary>
        public const float DefaultSlopeGlideRate = 5f;

        /// <summary>Default <see cref="SlopeGlideSnapDistance"/> (metres): 1.5.</summary>
        public const float DefaultSlopeGlideSnapDistance = 1.5f;
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
        sealed class Entry
        {
            public AnimatedCharacter Character = null!;
            public Vector3 PrevPosition;
            public bool HasPrev;
            public float Yaw;
            public Vector3 DispAccum;   // displacement summed within the current velocity window
            public float TimeAccum;     // elapsed time summed within the current velocity window
            public Vector3 Velocity;    // last closed-window velocity, held across zero-delta frames
            public float SmoothedY;     // slope-glide-smoothed feet height (see the smoother in Update); seeded to true
            public float GradeSumY;     // leaky-integrated vertical displacement (grade numerator AND mean-rate numerator)
            public float GradeSumXZ;    // leaky-integrated horizontal distance (grade window denominator, the glide gate)
            public float GradeSumT;     // leaky-integrated elapsed time (mean-rate denominator: climbRate = GradeSumY / GradeSumT)
            public bool SnapPending;    // a consumer called SnapRenderHeight: hard-cut the render height next Update
        }

        // --- Slope-fed render-height smoother constants (the two tunables live on CharacterAnimatorTuning) --------------
        // GRADE window: the dY/dXZ ratio is read from an exponentially-weighted sliding window of this length, so it
        // averages the per-riser sawtooth into the true mean grade. ~2 riser cadence cycles at walk (a cycle is ~0.23 s on
        // a 0.30/0.40 stair at walk) and ~3 at run - long enough to average the bob, short enough to re-establish the
        // grade within ~0.5 s at a stair top/bottom. Leaky (two O(1) accumulators), so no per-entity ring buffer/alloc.
        const float GradeWindowSeconds = 0.45f;
        // Below this |grade| (a ~3 degree slope) the time-paced glide is GATED OFF: the bob is sub-millimetre and the
        // damp-toward-true alone keeps flat ground byte-identical to the raw sample Y.
        const float MinGradeForGlide = 0.05f;
        // Clamp on the grade estimate (~56 degrees): above the 45 degree walkable-slope gate so real (even steep) stairs
        // still register as sloped, while rejecting the near-zero-horizontal ratio blow-up on a paused riser tick. Only
        // the GATE reads the grade now (the glide magnitude comes from the windowed mean RATE, not the grade), so the
        // clamp just keeps a paused-tick spike from tripping the gate oddly; it never scales the feed-forward.
        const float MaxGrade = 1.5f;
        // Exact |vertical velocity| above this (only when the sample carries exact movement) forces a SNAP, so a jump
        // takeoff / fall stays crisp. A grounded paced stair climb reports ~0 (the rise is a position adjustment, not a
        // ballistic velocity); a jump/fall reports several m/s - 2.0 sits cleanly between.
        const float BallisticVerticalSpeed = 2.0f;

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
        /// slope-glide smoother snaps the drawn feet-Y straight to the true feet-Y and restarts its grade + velocity
        /// windows from the destination, instead of gliding. No-op if <paramref name="id"/> is not tracked (it has not
        /// been sampled yet, or was dropped on disconnect).
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

        /// <summary>Advance every tracked character one frame from this frame's samples. Call once per render frame.</summary>
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
                    };
                    _entries[s.Id] = e;
                }

                // A consumer signalled an authoritative teleport for this id (SnapRenderHeight, wired to the netcode
                // teleport epoch): hard-cut the render height to the destination and restart the derivation from it, so
                // a SHORT blink under SlopeGlideSnapDistance cuts crisply instead of gliding (no height heuristic can
                // tell a short teleport from a stair riser - the consumer's signal is the only reliable source). Treat
                // the destination exactly like a fresh observation: seed SmoothedY at the true feet-Y, drop the stale
                // velocity + grade windows, and clear HasPrev so this frame derives no motion (and no feed-forward)
                // from the teleport delta. Cleared here; applies to exactly this one Update.
                if (e.SnapPending)
                {
                    e.SnapPending = false;
                    e.PrevPosition = s.Position;
                    e.HasPrev = false;
                    e.SmoothedY = s.Position.Y;
                    e.DispAccum = Vector3.Zero;
                    e.TimeAccum = 0f;
                    e.Velocity = Vector3.Zero;
                    e.GradeSumY = 0f;
                    e.GradeSumXZ = 0f;
                    e.GradeSumT = 0f;
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

                // SLOPE-FED render-height smoother: turn the paced stair-climb sim's per-riser vertical sawtooth (a
                // deliberate ~120-140 mm bob at 4-9 Hz - the sim is UNCHANGED) into a smooth glide up the stair slope,
                // for the drawn model (baked into World below) AND a follow camera (CharacterPose.RenderPosition). A plain
                // low-pass can't win here: attenuating a 4 Hz bob costs 15-22 cm of feet-float LAG on the ramp. Instead we
                // FEED FORWARD, but TIME-PACED: advance SmoothedY per frame by the windowed MEAN vertical climb RATE
                // (EWMA of dY/dt over the grade window), then critically damp SmoothedY toward the true feet-Y to correct
                // rate drift and settle onto real tread tops. The mean rate rides the ramp lag-free like a horizontal
                // feed-forward, but WITHOUT coupling to the per-tick horizontal - which is the whole point: on ASCENT the
                // paced step-up co-paces the horizontal along the stair tangent, so per-tick horizontal is ANTICORRELATED
                // with the rise (near-zero on rise ticks, a full tread on flat-tread ticks). The old horizontalDelta*grade
                // feed-forward multiplied that uneven horizontal by the grade and injected a per-frame vertical JUDDER
                // that read WORSE than the raw sawtooth on a run-up (measured: it doubled the worst single-frame pop). The
                // mean rate is horizontal-independent and smooth, so the glide advances uniformly in time up either slope
                // and beats the raw bob AND judder on all of walk-up / run-up / walk-down / run-down. Guards mirror
                // CharacterAvatar: !grounded / a ballistic vertical / a swim / a teleport-sized gap all SNAP to true so
                // jumps, falls, and ledge walk-offs stay crisp (never smoothed). A LARGE teleport (gap over the snap
                // distance) hard-cuts here automatically; a SHORT teleport under the snap distance is height-identical
                // to a stair riser and is cut only when the consumer calls SnapRenderHeight (the SnapPending path above),
                // which the netcode teleport epoch drives - see that method.
                float trueFeetY = s.Position.Y;
                if (_tuning.SlopeGlideRate > 0f && dt > 0f)
                {
                    Vector3 frameDelta = s.Position - e.PrevPosition;
                    float horizontalDelta = new Vector2(frameDelta.X, frameDelta.Z).Length();

                    // Windowed leaky accumulators (three O(1) sums, no per-entity ring buffer), same decay on all three.
                    // grade = dY / dXZ GATES the glide (a real slope vs flat); climbRate = dY / dt DRIVES it (the mean
                    // vertical speed). A stationary entity holds its grade (the ratio is decay-invariant) while its rate
                    // decays to zero (dt keeps accumulating, dY does not) - so the feed-forward fades and the damp settles.
                    float decay = MathF.Exp(-dt / GradeWindowSeconds);
                    e.GradeSumY = e.GradeSumY * decay + frameDelta.Y;
                    e.GradeSumXZ = e.GradeSumXZ * decay + horizontalDelta;
                    e.GradeSumT = e.GradeSumT * decay + dt;
                    float grade = e.GradeSumXZ > 1e-5f ? e.GradeSumY / e.GradeSumXZ : 0f;
                    grade = Math.Clamp(grade, -MaxGrade, MaxGrade);

                    bool ballistic = s.HasMovement && MathF.Abs(s.VerticalVelocity) > BallisticVerticalSpeed;
                    if (swimming || !grounded || ballistic
                        || MathF.Abs(trueFeetY - e.SmoothedY) > _tuning.SlopeGlideSnapDistance)
                    {
                        e.SmoothedY = trueFeetY;   // bypass / snap: crisp jumps, falls, teleports, swims
                    }
                    else
                    {
                        // Time-paced feed-forward: advance by the windowed mean climb RATE (lag-free ramp tracking, no
                        // per-tick horizontal MAGNITUDE coupling). GATED by the estimated grade - OFF below a ~3 degree
                        // slope so flat ground stays byte-identical. Also gated on THIS frame having horizontal motion
                        // (horizontalDelta > 0): feed-forward anticipates the ramp DURING travel, so when the entity is
                        // standing still there is nothing to feed forward - skipping it there lets the damp settle onto
                        // the tread promptly instead of the mean-rate window pushing the height on for ~0.5 s after a
                        // mid-stair stop (the rest-settle). It is a present/absent gate on motion, NOT the old
                        // magnitude coupling: the advance is still the smooth windowed rate, so a co-paced run-up (uneven
                        // per-tick horizontal, but nonzero every tick) still glides judder-free. Signed rate -> ascent
                        // raises, descent lowers, symmetrically.
                        if (MathF.Abs(grade) > MinGradeForGlide && horizontalDelta > 1e-6f)
                        {
                            float climbRate = e.GradeSumT > 1e-6f ? e.GradeSumY / e.GradeSumT : 0f;   // windowed mean dY/dt
                            e.SmoothedY += climbRate * dt;
                        }
                        // Critically damp toward the true feet-Y: corrects rate drift and settles onto real tread tops at
                        // rest. From an already-equal state (flat ground) this is a no-op, so render-Y == true Y exactly.
                        e.SmoothedY += (trueFeetY - e.SmoothedY) * (1f - MathF.Exp(-_tuning.SlopeGlideRate * dt));
                    }
                }
                else
                {
                    e.SmoothedY = trueFeetY;   // smoother disabled (rate <= 0) or a priming/paused tick: draw at true
                }

                Matrix4x4 world = Matrix4x4.CreateScale(_tuning.Scale)
                                  * Matrix4x4.CreateRotationY(e.Yaw)
                                  * Matrix4x4.CreateTranslation(s.Position.X, e.SmoothedY, s.Position.Z);
                _live.Add(new CharacterPose(s.Id, world, e.Character.Pose, e.Character.State, s.IsLocal));

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
