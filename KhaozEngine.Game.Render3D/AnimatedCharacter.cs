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
    /// position / vertical velocity / grounded flag. Client-cosmetic: never feed the pose back into
    /// simulation/netcode. A state with no clip in the map falls back to <see cref="LocomotionState.Idle"/> (and if
    /// Idle is also absent, the first supplied clip), so a partial clip set never throws.</summary>
    public sealed class AnimatedCharacter
    {
        /// <summary>Default seconds a newly-evaluated GROUND state must persist before it is committed (the
        /// <c>stateDebounceSeconds</c> ctor parameter). ~2.4 ticks at 30 Hz - long enough to reject a single-tick
        /// excursion in a derived movement signal, short enough to be imperceptible on a genuine transition.</summary>
        public const float DefaultStateDebounceSeconds = 0.08f;

        readonly AnimationPlayer _player;
        readonly IReadOnlyDictionary<LocomotionState, AnimationClip> _clips;
        readonly LocomotionThresholds _thresholds;
        readonly float _crossfade;
        readonly float _stateDebounce;
        readonly LocomotionSpeedSync _speedSync;
        readonly AnimationClip _fallback;
        Matrix4x4[] _pose;
        LocomotionState _candidate;   // last evaluated state awaiting commit
        float _candidateAge;          // seconds the candidate has differed from the committed State

        public LocomotionState State { get; private set; }

        /// <param name="skeleton">The rig the pose is evaluated on.</param>
        /// <param name="clips">One animation clip per locomotion state (idle/walk/run/jump/fall).</param>
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
            _player = new AnimationPlayer(skeleton);
            _pose = new Matrix4x4[skeleton.BoneCount];

            // Pose the first frame on Idle (no crossfade) so a character drawn before its first Update is not at rest-T.
            State = LocomotionState.Idle;
            _candidate = LocomotionState.Idle;
            _player.Play(ClipFor(LocomotionState.Idle), crossfade: 0f);
            _player.GetBonePalette(_pose);
        }

        /// <summary>Advance the animation for one frame from the movement state. A ground state (idle/walk/run) takes
        /// effect only after it has persisted for the debounce window, so a brief excursion in a derived movement
        /// signal does not restart the clip; air states (jump/fall) commit immediately. When <c>speedSync</c> is
        /// enabled a Walk/Run clip advances in proportion to <paramref name="horizontalSpeed"/> (Idle/air stay 1x).</summary>
        public void Update(float horizontalSpeed, bool grounded, float verticalVelocity, float dt)
        {
            LocomotionState evaluated = LocomotionStateMachine.Evaluate(horizontalSpeed, grounded, verticalVelocity, _thresholds);
            CommitState(evaluated, grounded, dt);
            _player.Play(ClipFor(State), _crossfade);
            _player.Update(dt, _speedSync.RateFor(State, horizontalSpeed));
            _player.GetBonePalette(_pose);
        }

        // Debounce ground-state transitions: a new ground state commits only after it has held continuously for the
        // debounce window, so a one-frame/one-tick flicker in the movement signal cannot restart the clip. Becoming
        // airborne (or switching air state) commits immediately - a real jump/fall must read instantly.
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
