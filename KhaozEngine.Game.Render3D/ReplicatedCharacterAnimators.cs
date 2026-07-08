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
    /// finite-differenced from the render position (no walk&lt;-&gt;idle flicker on a decel-to-stop).</summary>
    public readonly struct CharacterSample
    {
        /// <summary>Position-only sample: speed, vertical velocity, and grounded are all derived from the position
        /// delta vs the previous frame.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = false;
            Grounded = false;
            VerticalVelocity = 0f;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
        }

        /// <summary>Sample with exact movement (the local player): <see cref="Grounded"/> and
        /// <see cref="VerticalVelocity"/> are used as given instead of being derived.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
        }

        /// <summary>Fullest sample (the local player): exact <see cref="Grounded"/>, <see cref="VerticalVelocity"/>,
        /// AND exact planar <paramref name="planarSpeed"/> (m/s). The planar speed drives the locomotion state and the
        /// clip-speed sync DIRECTLY instead of being finite-differenced from the rendered position. Pass the clean
        /// commanded speed (<c>WorldClient.LocalHorizontalSpeed</c> / <c>ClientPrediction.PredictedHorizontalSpeed</c>):
        /// it is computed only on the prediction's commanded path, so it does not carry the reconciliation render offset
        /// and does not strobe walk&lt;-&gt;idle when the player decelerates to a stop (where the rendered position, even
        /// after the C1 smoothing fix, settles with a tiny residual sag). Facing still follows the derived heading
        /// (planar speed is magnitude-only). A negative value is treated as zero.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, float planarSpeed)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            HasPlanarSpeed = true;
            PlanarSpeed = planarSpeed;
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

        /// <summary>True when this sample carries an exact planar <see cref="PlanarSpeed"/> to use for the locomotion
        /// state instead of deriving it from the position delta.</summary>
        public bool HasPlanarSpeed { get; }

        /// <summary>Exact planar (ground-plane) speed, m/s (only meaningful when <see cref="HasPlanarSpeed"/>). Drives
        /// the idle/walk/run state and the clip-speed sync; facing still uses the derived heading.</summary>
        public float PlanarSpeed { get; }
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

        /// <summary>The world transform: <c>scale * RotationY(facingYaw) * Translation(position)</c>. The uniform
        /// scale is <see cref="CharacterAnimatorTuning.Scale"/> (default 1), so the consumer can draw with this
        /// matrix directly. The facing yaw assumes the asset's rest pose faces +Z; see
        /// <see cref="CharacterAnimatorTuning.FacingYawOffset"/> for assets that do not.</summary>
        public Matrix4x4 World { get; }

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

        /// <summary>Below this planar speed (m/s) the facing yaw is held (no spin at rest). Default 0.05.</summary>
        public float MinPlanarSpeedForFacing;

        /// <summary>When a sample carries no exact movement, |vertical velocity| below this (m/s) reads as grounded;
        /// above it the character is treated as airborne (jump/fall). Keeps small terrain-follow bumps grounded.
        /// Default 0.5.</summary>
        public float GroundedVerticalEpsilon;

        /// <summary>Uniform scale baked into <see cref="CharacterPose.World"/> so the consumer draws with that matrix
        /// directly. Default 1.</summary>
        public float Scale;

        /// <summary>Radians added to the derived facing yaw. The bridge faces an asset whose rest pose looks down +Z;
        /// set this (e.g. <see cref="MathF.PI"/>) for an asset authored facing another axis. Default 0.</summary>
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

        /// <summary>Lower clamp on the speed-sync playback multiplier (keeps a near-stationary entity from freezing
        /// the clip). Only used when <see cref="SyncLocomotionToSpeed"/> is set; 0 uses
        /// <see cref="LocomotionSpeedSync.DefaultMinMultiplier"/>. Default 0.25.</summary>
        public float MinLocomotionRate;

        /// <summary>Upper clamp on the speed-sync playback multiplier (keeps a teleporting entity from fast-forwarding
        /// the clip). Only used when <see cref="SyncLocomotionToSpeed"/> is set; 0 uses
        /// <see cref="LocomotionSpeedSync.DefaultMaxMultiplier"/>. Default 3.0.</summary>
        public float MaxLocomotionRate;

        /// <summary>The <see cref="LocomotionSpeedSync"/> these fields describe, applied to brains this set
        /// constructs. Disabled unless <see cref="SyncLocomotionToSpeed"/> is set.</summary>
        public readonly LocomotionSpeedSync SpeedSync() => SyncLocomotionToSpeed
            ? LocomotionSpeedSync.Enable(WalkClipSpeed, RunClipSpeed,
                MinLocomotionRate > 0f ? MinLocomotionRate : LocomotionSpeedSync.DefaultMinMultiplier,
                MaxLocomotionRate > 0f ? MaxLocomotionRate : LocomotionSpeedSync.DefaultMaxMultiplier)
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
            MinLocomotionRate = LocomotionSpeedSync.DefaultMinMultiplier,
            MaxLocomotionRate = LocomotionSpeedSync.DefaultMaxMultiplier,
        };
    }

    /// <summary>Owns one <see cref="AnimatedCharacter"/> per replicated entity and turns a per-frame stream of
    /// <see cref="CharacterSample"/>s into draw-ready <see cref="CharacterPose"/>s. The reusable bridge between
    /// "the netcode hands me positions" and "drive an animated avatar per player" - for the local player AND every
    /// remote, since position-over-time is the one signal every netcode surfaces for every entity.
    ///
    /// Per <see cref="Update"/>: a new id is created via the factory; a tracked id absent from the samples is dropped
    /// (no leak on disconnect); planar speed / vertical velocity / facing are derived from the position displacement
    /// averaged over a short window (so a plateauing / zero-delta position stream does not strobe the state; the
    /// exact grounded flag + vertical velocity are used instead when the sample <see cref="CharacterSample.HasMovement"/>);
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
        }

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
                        Yaw = 0f,
                    };
                    _entries[s.Id] = e;
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

                // Facing: aim along the derived planar heading, but only while the entity is genuinely moving. The
                // derived heading (from the render-position delta) swings around during the post-stop render settle -
                // the local avatar's rendered position sags backward then recovers, so the delta briefly points
                // backward/sideways - and chasing it spins the model for a few frames before it corrects. So gate on the
                // EXACT planar speed too when it is supplied (the local player): at a real stop it is 0, holding the yaw
                // through the settle. Remotes (no exact speed) gate on the derived speed alone as before. The derived
                // magnitude is still required so there is a valid heading direction for the Atan2. Below the threshold
                // the yaw holds (no spin at rest).
                bool movingForFacing = derivedPlanarSpeed > _tuning.MinPlanarSpeedForFacing
                    && (!s.HasPlanarSpeed || locomotionSpeed > _tuning.MinPlanarSpeedForFacing);
                if (movingForFacing)
                {
                    float target = MathF.Atan2(planarVel.X, planarVel.Z) + _tuning.FacingYawOffset;
                    e.Yaw = LerpAngle(e.Yaw, target, _tuning.YawSmoothing);
                }

                e.Character.Update(locomotionSpeed, grounded, verticalVelocity, dt);

                Matrix4x4 world = Matrix4x4.CreateScale(_tuning.Scale)
                                  * Matrix4x4.CreateRotationY(e.Yaw)
                                  * Matrix4x4.CreateTranslation(s.Position);
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
