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
        public void Play(AnimationClip clip, float crossfade = 0.15f)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (ReferenceEquals(clip, _to)) return;   // already playing this clip: keep the playhead, no blend
            _from = _to;
            _fromTime = _toTime;
            _to = clip;
            _toTime = 0f;
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
        /// multiplier lets a caller sync clip playback to movement speed (see
        /// <see cref="KhaozEngine.Game.LocomotionSpeedSync"/>) without changing how long a crossfade takes: the clip
        /// playheads (both the incoming and the outgoing clip during a blend) move at the scaled rate so the feet
        /// track speed even mid-blend, but the crossfade TIMER always runs at wall-clock <paramref name="dt"/> so a
        /// blend still completes in its authored duration regardless of speed. <paramref name="speedMultiplier"/> 1
        /// (the default path via <see cref="Update(float)"/>) is byte-identical to the pre-speed-sync behaviour.</summary>
        public void Update(float dt, float speedMultiplier)
        {
            if (_to is null) return;
            float clipDt = dt * speedMultiplier;   // clip playheads advance at the scaled rate...
            _toTime = AnimationSampler.Wrap(_toTime + clipDt, _to.Duration);
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
            if (_to is null) { _skeleton.ComposeInto(_skeleton.RestLocal, outPalette); return; }

            JointPose[] toPose = AnimationSampler.SamplePose(_to, _skeleton, _toTime);
            if (_from != null && _blend < 1f)
            {
                JointPose[] fromPose = AnimationSampler.SamplePose(_from, _skeleton, _fromTime);
                JointPose[] blended = AnimationSampler.BlendPoses(fromPose, toPose, _blend);
                _skeleton.ComposeInto(blended, outPalette);
            }
            else
            {
                _skeleton.ComposeInto(toPose, outPalette);
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
