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
        readonly AnimationPlayer _player;
        readonly IReadOnlyDictionary<LocomotionState, AnimationClip> _clips;
        readonly LocomotionThresholds _thresholds;
        readonly float _crossfade;
        readonly AnimationClip _fallback;
        Matrix4x4[] _pose;

        public LocomotionState State { get; private set; }

        public AnimatedCharacter(Skeleton skeleton, IReadOnlyDictionary<LocomotionState, AnimationClip> clips,
            LocomotionThresholds? thresholds = null, float crossfade = 0.15f)
        {
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            _clips = clips ?? throw new ArgumentNullException(nameof(clips));
            if (clips.Count == 0) throw new ArgumentException("at least one clip is required.", nameof(clips));
            _thresholds = thresholds ?? LocomotionThresholds.Default;
            _crossfade = crossfade;
            _fallback = ResolveFallback(clips);
            _player = new AnimationPlayer(skeleton);
            _pose = new Matrix4x4[skeleton.BoneCount];

            // Pose the first frame on Idle (no crossfade) so a character drawn before its first Update is not at rest-T.
            State = LocomotionState.Idle;
            _player.Play(ClipFor(LocomotionState.Idle), crossfade: 0f);
            _player.GetBonePalette(_pose);
        }

        /// <summary>Advance the animation for one frame from the movement state.</summary>
        public void Update(float horizontalSpeed, bool grounded, float verticalVelocity, float dt)
        {
            State = LocomotionStateMachine.Evaluate(horizontalSpeed, grounded, verticalVelocity, _thresholds);
            _player.Play(ClipFor(State), _crossfade);
            _player.Update(dt);
            _player.GetBonePalette(_pose);
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
