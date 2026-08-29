using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
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

        /// <summary>The live characters this frame, in sample order, EXACTLY ONE PER ENTITY ID (a repeated id in the
        /// sample list is dropped, see <see cref="Update"/>). Iterate and draw each with
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
        /// collapse to prone) instead of the locomotion path.
        /// <para><paramref name="samples"/> is expected to carry at most ONE entry per
        /// <see cref="CharacterSample.Id"/>, which is what the netcode's own dictionary-keyed snapshot yields. A list
        /// assembled another way (two sources concatenated without a dedup, say) may repeat one, and a repeat is
        /// DROPPED: the first entry for an id is the one advanced and posed, so <see cref="Live"/> holds exactly one
        /// pose per entity whatever the caller hands in.</para></summary>
        public void Update(IReadOnlyList<CharacterSample> samples, float dt)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            _live.Clear();
            _seen.Clear();

            for (int i = 0; i < samples.Count; i++)
            {
                CharacterSample s = samples[i];
                // ONE POSE PER ID, ENFORCED AT THE ENTRY (#97). `_seen` is this frame's id set and `Live` is
                // documented as one pose per live entity, so a repeated id is dropped HERE rather than left to reach
                // a pose branch. Letting it through costs twice: both branches push unconditionally, so the consumer
                // iterating Live draws the entity twice, and the entry's velocity window, glide smoother and step
                // baseline all age a second time against the same frame's dt, which is a state corruption that
                // outlives the frame. First sample for an id wins.
                if (!_seen.Add(s.Id)) continue;

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

                // The sector rides through untouched, alongside the speed rather than inside it. That is deliberate:
                // locomotionSpeed above is clamped non-negative BECAUSE the state machine reads it, so a reverse move
                // cannot be smuggled in as a negative speed without reading as Idle. The direction travels as its own
                // field and is consumed only by the playback rate (see AnimatedCharacter.Update). A sample that never
                // classifies one is MoveSector.Forward, and the brain ignores the sector unless its speed sync opted in.
                e.Character.Update(locomotionSpeed, grounded, verticalVelocity, swimming, s.Sector, dt);

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
