using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
    /// <summary>A skinned character's animation brain: wraps a <see cref="Render3D.Skeleton"/>, the per-state
    /// locomotion clips, an <see cref="AnimationPlayer"/>, and the <see cref="LocomotionStateMachine"/>. Fed a
    /// movement state (horizontal speed, grounded, vertical velocity) + dt each frame, it picks the locomotion clip,
    /// crossfades into it, advances the playhead, and produces the joint-WORLD bone palette for
    /// <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Primitives.Color)"/>.
    /// Drive one per character - the LOCAL player from its own movement, each REMOTE player from its replicated
    /// position / vertical velocity / grounded / swimming flags. Client-cosmetic: never feed the pose back into
    /// simulation/netcode. A state with no clip in the map falls back to <see cref="LocomotionState.Idle"/> (and if
    /// Idle is also absent, the first supplied clip), so a partial clip set never throws - a consumer that has not
    /// yet baked the water clips (<c>Swim</c> / <c>SwimIdle</c>) degrades to Idle while swimming rather than crashing.</summary>
    public sealed class AnimatedCharacter
    {
        /// <summary>Default seconds a newly-evaluated GROUND state must persist before it is committed (the
        /// <c>stateDebounceSeconds</c> ctor parameter). ~2.4 ticks at 30 Hz - long enough to reject a single-tick
        /// excursion in a derived movement signal, short enough to be imperceptible on a genuine transition.</summary>
        public const float DefaultStateDebounceSeconds = 0.08f;

        readonly AnimationPlayer _player;
        readonly Skeleton _skeleton;
        readonly IReadOnlyDictionary<LocomotionState, AnimationClip> _clips;
        readonly LocomotionThresholds _thresholds;
        readonly float _crossfade;
        readonly float _stateDebounce;
        readonly LocomotionSpeedSync _speedSync;
        readonly AnimationClip _fallback;
        Matrix4x4[] _pose;
        LocomotionState _candidate;   // last evaluated state awaiting commit
        float _candidateAge;          // seconds the candidate has differed from the committed State

        // Lazily built the first time PlayAction is called: the action compositor that stacks masked one-shot actions
        // (attacks, casts) over the locomotion base. Null until then, so a character that never plays an action carries
        // no extra state and takes the byte-stable single-player path.
        LayeredAnimator? _actions;
        JointPose[]? _baseLocals;   // scratch for the locomotion crossfade fed to _actions as the base each frame

        // True between EnterDowned and ExitDowned WHEN a Downed clip exists: the downed clip is playing clamped (held
        // on its final frame) and UpdateDowned advances it. Without a Downed clip the downed pose is procedural (the
        // bridge tips the world transform to prone) and the frozen locomotion pose holds, so this stays false.
        bool _downedClipPlaying;

        public LocomotionState State { get; private set; }

        /// <param name="skeleton">The rig the pose is evaluated on.</param>
        /// <param name="clips">One animation clip per locomotion state (idle/walk/run/jump/fall, plus the water clips
        /// swim/swimIdle). A missing state falls back to Idle (then the first clip), so a partial set never throws.</param>
        /// <param name="thresholds">Speed cutoffs mapping a planar speed to a ground state. Null uses defaults.</param>
        /// <param name="crossfade">Seconds to blend from the previous clip on a state switch.</param>
        /// <param name="stateDebounceSeconds">Seconds a newly-evaluated GROUND state must persist before it is
        /// committed and the clip switches. A brief excursion in the movement signal (e.g. a one-tick spike in a
        /// position-derived speed - which the replicated-animator bridge sees from the prediction/reconcile render
        /// stream) would otherwise flip the state and restart the clip every time it happens. Air states (jump/fall)
        /// are exempt and commit immediately, so a real jump never lags. Default
        /// <see cref="DefaultStateDebounceSeconds"/>; pass 0 to commit immediately (the pre-7.68.0 behaviour).</param>
        /// <param name="speedSync">Opt-in speed-synced playback (see <see cref="LocomotionSpeedSync"/>). Default
        /// (<c>default</c> / <see cref="LocomotionSpeedSync.Disabled"/>) is OFF: every ground/air clip plays at its
        /// authored rate, byte-identical to the pre-speed-sync behaviour. Enabled, a Walk/Run clip advances in
        /// proportion to <c>horizontalSpeed</c> so its feet stop sliding; Idle and air states still play at 1x.</param>
        public AnimatedCharacter(Skeleton skeleton, IReadOnlyDictionary<LocomotionState, AnimationClip> clips,
            LocomotionThresholds? thresholds = null, float crossfade = 0.15f,
            float stateDebounceSeconds = DefaultStateDebounceSeconds,
            LocomotionSpeedSync speedSync = default)
        {
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            _clips = clips ?? throw new ArgumentNullException(nameof(clips));
            if (clips.Count == 0) throw new ArgumentException("at least one clip is required.", nameof(clips));
            _thresholds = thresholds ?? LocomotionThresholds.Default;
            _crossfade = crossfade;
            _stateDebounce = MathF.Max(0f, stateDebounceSeconds);
            _speedSync = speedSync;
            _fallback = ResolveFallback(clips);
            _skeleton = skeleton;
            _player = new AnimationPlayer(skeleton);
            _pose = new Matrix4x4[skeleton.BoneCount];

            // Pose the first frame on Idle (no crossfade) so a character drawn before its first Update is not at rest-T.
            State = LocomotionState.Idle;
            _candidate = LocomotionState.Idle;
            _player.Play(ClipFor(LocomotionState.Idle), crossfade: 0f);
            _player.GetBonePalette(_pose);
        }

        /// <summary>Advance the animation for one frame from the movement state, never swimming (the pre-swim
        /// overload). Kept so existing callers compile bit-identically.</summary>
        public void Update(float horizontalSpeed, bool grounded, float verticalVelocity, float dt) =>
            Update(horizontalSpeed, grounded, verticalVelocity, swimming: false, dt);

        /// <summary>Advance the animation for one frame from the movement state. A ground state (idle/walk/run) takes
        /// effect only after it has persisted for the debounce window, so a brief excursion in a derived movement
        /// signal does not restart the clip; air states (jump/fall) AND the water states (swim/tread) commit
        /// immediately - a real jump/fall or a swim enter/exit must read instantly (the swim enter/exit is already
        /// hysteresis-debounced in the movement sim, so a second debounce here would only add lag). When
        /// <paramref name="swimming"/> is set the state machine selects the tread <see cref="LocomotionState.SwimIdle"/>
        /// or the forward <see cref="LocomotionState.Swim"/> from the planar speed (the swim flag is threaded from the
        /// movement medium, never re-queried). When <c>speedSync</c> is enabled a Walk/Run/Swim clip advances in
        /// proportion to <paramref name="horizontalSpeed"/> (Idle/tread/air stay 1x).</summary>
        public void Update(float horizontalSpeed, bool grounded, float verticalVelocity, bool swimming, float dt)
        {
            LocomotionState evaluated = LocomotionStateMachine.Evaluate(horizontalSpeed, grounded, verticalVelocity, swimming, _thresholds);
            CommitState(evaluated, grounded && !swimming, dt);
            _player.Play(ClipFor(State), _crossfade);
            _player.Update(dt, _speedSync.RateFor(State, horizontalSpeed));

            // No action in flight: the byte-stable single-player path (bit-identical to pre-action behaviour). One
            // action or more: feed the locomotion crossfade to the compositor as the base layer and stack the actions.
            if (_actions is null || !_actions.HasActiveActions)
            {
                _player.GetBonePalette(_pose);
            }
            else
            {
                _player.GetLocalPoses(_baseLocals!);   // the locomotion crossfade result, as LOCAL poses
                _actions.SetBaseLocals(_baseLocals);
                _actions.Update(dt);                    // step the action fades / retires
                _actions.GetBonePalette(_pose);         // composite actions over the locomotion base
            }
        }

        /// <summary>Play <paramref name="clip"/> as an action stacked over locomotion (an attack, a cast). With
        /// <paramref name="hold"/> false (default) it is a ONE-SHOT: fade in, play through, fade out overlapping the clip
        /// tail, then auto-retire. With <paramref name="hold"/> true it is HELD indefinitely: after the fade-in it stays
        /// at full weight and loops (a persistent masked pose, e.g. a drawn-weapon arm idle held over locomotion) until
        /// <see cref="CancelAction"/> fades it out. <paramref name="mask"/> gates it to a body region (e.g.
        /// <c>BoneMask.Subtree(Skeleton, spineNode, 1f)</c> for an upper-body attack while the legs keep running);
        /// null == the whole skeleton. Returns an <see cref="ActionHandle"/> for <see cref="CancelAction"/>. Callable on
        /// a LOCAL or a REMOTE character's brain alike - drive a remote's action by calling this when the game receives
        /// the replicated action trigger (replicating the trigger is a game-message concern, out of scope here).
        /// <paramref name="speed"/> scales the playhead (the real play duration is <c>clip.Duration / speed</c>), while
        /// <paramref name="fadeIn"/> / <paramref name="fadeOut"/> are wall-clock seconds independent of
        /// <paramref name="speed"/>. The slot pool grows when no idle slot exists, so an action is never rejected. When
        /// two live actions mask the same bone they composite by layer stack order (higher slot index wins), which after
        /// slot reuse is slot-acquisition order, not play order - do not rely on play-order precedence for overlapping
        /// masks; a held action played FIRST sits below later one-shot actions, which composite over it and fall back to
        /// it as they retire. <paramref name="hold"/> true holds the action indefinitely at full weight (looping) instead
        /// of playing it once; the auto fade-out is suppressed and it ends only via <see cref="CancelAction"/>. Default
        /// false (one-shot). Client-cosmetic: never feed the pose back into simulation/netcode.</summary>
        public ActionHandle PlayAction(AnimationClip clip, BoneMask? mask = null, float fadeIn = 0.1f, float fadeOut = 0.1f,
            float speed = 1f, LayerMode mode = LayerMode.Override, bool hold = false)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (_actions is null)
            {
                _actions = new LayeredAnimator(_skeleton);
                _baseLocals = new JointPose[_skeleton.NodeCount];
            }
            return _actions.PlayAction(clip, mask, fadeIn, fadeOut, speed, mode, hold);
        }

        /// <summary>Cancel an in-flight action early: fade it out cleanly from its current weight (no pose pop). A no-op
        /// for a stale/defaulted handle. Returns true if it referred to a live action.</summary>
        public bool CancelAction(ActionHandle handle) => _actions?.Cancel(handle) ?? false;

        /// <summary>Cancel every in-flight action at once (fading or held) - e.g. before a downed / death pose so
        /// nothing keeps playing underneath it. <paramref name="immediate"/> false (default) fades each one out
        /// gracefully from its current weight, true retires them all this instant with no fade. A no-op if no action
        /// has ever been played.</summary>
        public void CancelAllActions(bool immediate = false) => _actions?.CancelAllActions(immediate);

        /// <summary>True while at least one one-shot action is fading in, playing, or fading out.</summary>
        public bool HasActiveActions => _actions is not null && _actions.HasActiveActions;

        /// <summary>The rig this character poses (for building a <see cref="BoneMask"/> to pass to
        /// <see cref="PlayAction"/>).</summary>
        public Skeleton Skeleton => _skeleton;

        /// <summary>True when a clip is baked for <see cref="LocomotionState.Downed"/> (a death / knockdown pose, by the
        /// name-based clip convention). <see cref="ReplicatedCharacterAnimators"/> checks this to choose the downed
        /// presentation: a rig WITH the clip plays it once and holds its final frame (<see cref="EnterDowned"/> +
        /// <see cref="UpdateDowned"/>); a rig WITHOUT it collapses procedurally (the bridge tips the world transform to
        /// prone) while this brain just freezes its pose.</summary>
        public bool HasDownedClip => _clips.ContainsKey(LocomotionState.Downed);

        /// <summary>Enter the downed / death pose - call ONCE on the downed rising edge (the bridge does). With a
        /// <see cref="LocomotionState.Downed"/> clip present it starts that clip playing ONCE, crossfading in from the
        /// current locomotion pose, then holding its final frame (see <see cref="UpdateDowned"/>). With no Downed clip
        /// it SNAPS to the neutral fallback (Idle) pose and freezes there, so the bridge's procedural collapse tips a
        /// clean rest pose to prone rather than a mid-stride limb tangle. <see cref="State"/> reads
        /// <see cref="LocomotionState.Downed"/> from here until <see cref="ExitDowned"/>. Locomotion selection and any
        /// stacked actions are suppressed while downed (neither <see cref="Update(float, bool, float, bool, float)"/>
        /// nor the action compositor runs - the bridge calls <see cref="UpdateDowned"/> instead).</summary>
        public void EnterDowned()
        {
            // Cancel any in-flight action immediately (no fade): UpdateDowned never advances the compositor, so a
            // graceful fade would freeze mid-fade and resume unfaded on the first post-respawn Update instead.
            _actions?.CancelAllActions(immediate: true);

            if (HasDownedClip)
            {
                _downedClipPlaying = true;
                _player.PlayOnce(_clips[LocomotionState.Downed], _crossfade);
            }
            else
            {
                _downedClipPlaying = false;
                _player.Play(_fallback, crossfade: 0f);   // snap to the neutral pose; the bridge tips it to prone
                _player.GetBonePalette(_pose);
            }
            State = LocomotionState.Downed;
        }

        /// <summary>Advance the downed pose one frame - call each frame while downed (the bridge does). With a Downed
        /// clip it advances the clamped (non-looping) playhead, so the clip plays through once and then HOLDS its final
        /// frame indefinitely. With no Downed clip the frozen fallback pose is left untouched. Actions do NOT composite
        /// while downed. <see cref="State"/> stays <see cref="LocomotionState.Downed"/>.</summary>
        public void UpdateDowned(float dt)
        {
            State = LocomotionState.Downed;
            if (!_downedClipPlaying) return;   // procedural: the frozen pose holds, nothing to advance
            _player.Update(dt);
            _player.GetBonePalette(_pose);
        }

        /// <summary>Leave the downed pose and return to normal locomotion - call ONCE on the downed falling edge (the
        /// bridge does, on respawn / revive). SNAPS the player back to the neutral fallback (Idle) with no crossfade so
        /// no death-pose residual lingers into the respawned character (a respawn usually teleports, so a get-up blend
        /// is not wanted). The next <see cref="Update(float, bool, float, bool, float)"/> resumes normal locomotion
        /// selection from there.</summary>
        public void ExitDowned()
        {
            _downedClipPlaying = false;
            _player.Play(_fallback, crossfade: 0f);   // snap to neutral, restoring looping playback
            _player.GetBonePalette(_pose);
            State = LocomotionState.Idle;
            _candidate = LocomotionState.Idle;
            _candidateAge = 0f;
        }

        // Debounce ground-state transitions: a new ground state commits only after it has held continuously for the
        // debounce window, so a one-frame/one-tick flicker in the movement signal cannot restart the clip. Becoming
        // airborne (or switching air state) commits immediately - a real jump/fall must read instantly. The caller
        // passes false for the "grounded" gate while swimming too, so swim/tread transitions also commit immediately
        // (the swim flag is already hysteresis-debounced in the movement sim; a second debounce here would only lag it).
        void CommitState(LocomotionState evaluated, bool grounded, float dt)
        {
            if (evaluated == State) { _candidate = State; _candidateAge = 0f; return; }
            if (!grounded || _stateDebounce <= 0f) { State = evaluated; _candidate = evaluated; _candidateAge = 0f; return; }
            if (evaluated != _candidate) { _candidate = evaluated; _candidateAge = 0f; }
            _candidateAge += dt;
            if (_candidateAge >= _stateDebounce) { State = _candidate; _candidateAge = 0f; }
        }

        /// <summary>The current joint-WORLD bone palette (length skeleton bone count). Feed to
        /// <c>Scene3D.DrawSkinned</c>. The returned array is reused each frame; copy it if you need to retain it.</summary>
        public Matrix4x4[] Pose => _pose;

        /// <summary>Copy the current bone palette into <paramref name="dst"/> (length must equal bone count).</summary>
        public void CopyPose(Matrix4x4[] dst)
        {
            if (dst is null) throw new ArgumentNullException(nameof(dst));
            if (dst.Length != _pose.Length) throw new ArgumentException("dst length must equal the bone count.", nameof(dst));
            Array.Copy(_pose, dst, _pose.Length);
        }

        AnimationClip ClipFor(LocomotionState state) =>
            _clips.TryGetValue(state, out AnimationClip? clip) ? clip : _fallback;

        static AnimationClip ResolveFallback(IReadOnlyDictionary<LocomotionState, AnimationClip> clips)
        {
            if (clips.TryGetValue(LocomotionState.Idle, out AnimationClip? idle)) return idle;
            foreach (AnimationClip c in clips.Values) return c;   // first supplied clip
            throw new ArgumentException("at least one clip is required.", nameof(clips));
        }
    }
}
