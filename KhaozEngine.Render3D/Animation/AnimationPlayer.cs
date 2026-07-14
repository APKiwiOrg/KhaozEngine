using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Stateful clip playback for one skinned character: holds the current clip + a looping playhead,
    /// advances it by <c>dt</c>, and CROSSFADES into a new clip over a short blend (the previous clip and the new
    /// clip both keep advancing during the blend; their per-node local poses are interpolated by the blend weight,
    /// then composed once). Produces the joint-WORLD bone palette
    /// <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Primitives.Color)"/>
    /// consumes. Presentation only; GPU-free; not thread-safe (one per character).</summary>
    public sealed class AnimationPlayer
    {
        readonly Skeleton _skeleton;

        AnimationClip? _to;
        float _toTime;
        bool _toLoops = true;   // false == the current clip clamps at its Duration (one-shot hold), see Play/PlayOnce
        AnimationClip? _from;
        float _fromTime;
        float _blend;        // 0..1 progress of the from->to crossfade (1 == done)
        float _blendDur;     // crossfade duration in seconds (0 == snap)

        public AnimationPlayer(Skeleton skeleton)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        }

        /// <summary>The clip currently playing (the crossfade target), or null before the first <see cref="Play"/>.</summary>
        public AnimationClip? Current => _to;

        /// <summary>True while a crossfade from a previous clip into <see cref="Current"/> is in progress.</summary>
        public bool IsBlending => _from != null && _blend < 1f;

        /// <summary>The current clip's playhead time (seconds, looped within the clip duration).</summary>
        public float Time => _toTime;

        /// <summary>Switch to <paramref name="clip"/>. If it is already the current clip this is a no-op (the
        /// playhead is not reset). Otherwise the current pose becomes the crossfade source and the new clip starts
        /// from time 0, blending in over <paramref name="crossfade"/> seconds (an immediate snap when there is no
        /// current clip or <paramref name="crossfade"/> &lt;= 0).</summary>
        public void Play(AnimationClip clip, float crossfade = 0.15f) => SwitchTo(clip, crossfade, loop: true);

        /// <summary>Like <see cref="Play(AnimationClip, float)"/> but plays <paramref name="clip"/> ONCE: the playhead
        /// CLAMPS at the clip's <see cref="AnimationClip.Duration"/> instead of looping, so once it reaches the end it
        /// HOLDS the final frame (the tracks clamp to their end keys). The clean primitive for a one-shot pose that must
        /// settle and stay - a death / knockdown pose held on its last frame. Switching to any clip via
        /// <see cref="Play(AnimationClip, float)"/> restores looping. If it is already the current clip the playhead is
        /// kept (no restart) but the clamp mode is (re)asserted, so re-asserting a hold on the clip already playing does
        /// not rewind it.</summary>
        public void PlayOnce(AnimationClip clip, float crossfade = 0.15f) => SwitchTo(clip, crossfade, loop: false);

        void SwitchTo(AnimationClip clip, float crossfade, bool loop)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (ReferenceEquals(clip, _to)) { _toLoops = loop; return; }   // already playing: keep the playhead, no blend
            _from = _to;
            _fromTime = _toTime;
            _to = clip;
            _toTime = 0f;
            _toLoops = loop;
            bool canBlend = _from != null && crossfade > 0f;
            _blendDur = canBlend ? crossfade : 0f;
            _blend = canBlend ? 0f : 1f;
            if (!canBlend) _from = null;
        }

        /// <summary>Advance the playhead(s) by <paramref name="dt"/> seconds (looping each clip) and progress any
        /// crossfade.</summary>
        public void Update(float dt) => Update(dt, 1f);

        /// <summary>Advance the playhead(s) by <paramref name="dt"/> * <paramref name="speedMultiplier"/> seconds
        /// (looping each clip) while progressing any crossfade at the REAL <paramref name="dt"/>. Scaling the
        /// multiplier lets a caller sync clip playback to movement speed (e.g. <c>LocomotionSpeedSync</c> in
        /// KhaozEngine.Game.Render3D) without changing how long a crossfade takes: the clip
        /// playheads (both the incoming and the outgoing clip during a blend) move at the scaled rate so the feet
        /// track speed even mid-blend, but the crossfade TIMER always runs at wall-clock <paramref name="dt"/> so a
        /// blend still completes in its authored duration regardless of speed. <paramref name="speedMultiplier"/> 1
        /// (the default path via <see cref="Update(float)"/>) is byte-identical to the pre-speed-sync behaviour.</summary>
        public void Update(float dt, float speedMultiplier)
        {
            if (_to is null) return;
            float clipDt = dt * speedMultiplier;   // clip playheads advance at the scaled rate...
            // A looping clip wraps within its duration. A one-shot (PlayOnce) clamps at the end and HOLDS the final
            // frame there. The FROM clip during a crossfade always wraps (it is fading out, never a held pose).
            _toTime = _toLoops
                ? AnimationSampler.Wrap(_toTime + clipDt, _to.Duration)
                : MathF.Min(_toTime + clipDt, _to.Duration);
            if (_from != null)
            {
                _fromTime = AnimationSampler.Wrap(_fromTime + clipDt, _from.Duration);
                if (_blendDur > 0f)
                {
                    _blend += dt / _blendDur;   // ...but the crossfade TIMER runs at wall-clock dt (blend duration is speed-independent)
                    if (_blend >= 1f) { _blend = 1f; _from = null; }
                }
                else { _blend = 1f; _from = null; }
            }
        }

        /// <summary>Write the current joint-WORLD bone palette into <paramref name="outPalette"/> (length
        /// <see cref="Skeleton.BoneCount"/>). Before the first <see cref="Play"/> this is the rest pose.</summary>
        public void GetBonePalette(Matrix4x4[] outPalette)
        {
            if (outPalette is null) throw new ArgumentNullException(nameof(outPalette));
            GetLocalPoses(_localScratch());
            _skeleton.ComposeInto(_localsBuf!, outPalette);
        }

        // Reused scratch for the composited LOCAL poses (crossfade result), so GetBonePalette / GetLocalPoses do not
        // allocate a per-frame pose array. Grown once to the skeleton node count.
        JointPose[]? _localsBuf;
        JointPose[]? _fromBuf;
        JointPose[] _localScratch()
        {
            if (_localsBuf is null || _localsBuf.Length != _skeleton.NodeCount) _localsBuf = new JointPose[_skeleton.NodeCount];
            return _localsBuf;
        }

        /// <summary>Write the current composited LOCAL poses (the crossfade result, one per skeleton node, BEFORE
        /// hierarchy composition) into <paramref name="outLocals"/> (length <see cref="Skeleton.NodeCount"/>). This is
        /// the exact intermediate <see cref="GetBonePalette"/> composes, exposed so a <see cref="LayeredAnimator"/> can
        /// take the locomotion crossfade as its BASE layer and stack masked action layers on top (attack-while-running)
        /// while the base stays byte-identical to the single-clip path. Before the first <see cref="Play"/> this is the
        /// rest pose. Steady-state allocation-free.</summary>
        public void GetLocalPoses(JointPose[] outLocals)
        {
            if (outLocals is null) throw new ArgumentNullException(nameof(outLocals));
            if (outLocals.Length != _skeleton.NodeCount)
                throw new ArgumentException($"outLocals length {outLocals.Length} must equal node count {_skeleton.NodeCount}.", nameof(outLocals));

            if (_to is null)
            {
                for (int n = 0; n < outLocals.Length; n++) outLocals[n] = _skeleton.RestLocal[n];
                return;
            }

            AnimationSampler.SampleInto(_to, _skeleton, _toTime, outLocals);
            if (_from != null && _blend < 1f)
            {
                if (_fromBuf is null || _fromBuf.Length != _skeleton.NodeCount) _fromBuf = new JointPose[_skeleton.NodeCount];
                AnimationSampler.SampleInto(_from, _skeleton, _fromTime, _fromBuf);
                for (int n = 0; n < outLocals.Length; n++) outLocals[n] = JointPose.Lerp(_fromBuf[n], outLocals[n], _blend);
            }
        }

        /// <summary>Allocate + return the current joint-WORLD bone palette. Prefer <see cref="GetBonePalette"/> with
        /// a reused buffer in the per-frame draw path.</summary>
        public Matrix4x4[] BonePalette()
        {
            var palette = new Matrix4x4[_skeleton.BoneCount];
            GetBonePalette(palette);
            return palette;
        }
    }
}
