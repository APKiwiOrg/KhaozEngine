using System;

namespace KhaozEngine.Render3D
{
    /// <summary>How an <see cref="AnimationLayer"/> combines with the poses below it.</summary>
    public enum LayerMode
    {
        /// <summary>Replace: the composited node is lerped from the base pose TOWARD the layer's sampled pose by
        /// weight x mask (weight 1 + mask 1 == fully the layer pose). The mode a masked upper-body ACTION uses (an
        /// attack fully drives the arms while the legs stay on locomotion).</summary>
        Override,

        /// <summary>Add: the layer contributes its DELTA from a reference frame (its first frame), scaled by
        /// weight x mask, on top of the base. Rotations compose multiplicatively (delta = sample * inverse(reference),
        /// applied left of the base); translation/scale-offset add. The mode a stackable modifier uses (an additive
        /// lean or recoil layered over whatever plays beneath).</summary>
        Additive,
    }

    /// <summary>One entry in a <see cref="LayeredAnimator"/> stack: a clip with its own looping playhead (advanced the
    /// same way <see cref="AnimationPlayer"/> advances its clip - <see cref="AnimationSampler.Wrap"/> at a per-layer
    /// speed), a blend <see cref="Weight"/> in [0,1], an optional <see cref="Mask"/> (null == full skeleton), and a
    /// <see cref="LayerMode"/>. Presentation only; GPU-free; not thread-safe (one stack per character).</summary>
    public sealed class AnimationLayer
    {
        JointPose[]? _referencePose;   // additive reference (clip's first frame), sampled once per (clip, skeleton)
        AnimationClip? _referenceClip; // the clip _referencePose was sampled from (re-sampled if the clip changes)

        /// <summary>The clip this layer samples. Never null.</summary>
        public AnimationClip Clip { get; private set; }

        /// <summary>The layer's looping playhead time (seconds, wrapped within <see cref="AnimationClip.Duration"/>).</summary>
        public float Time { get; set; }

        /// <summary>Playback rate multiplier for this layer's playhead (1 == authored rate). Advances the playhead by
        /// <c>dt * Speed</c> in <see cref="Update"/>, mirroring <see cref="AnimationPlayer.Update(float, float)"/>.</summary>
        public float Speed { get; set; } = 1f;

        /// <summary>Blend weight in [0,1] (clamped on set): 0 contributes nothing (the fast-path skip), 1 contributes
        /// fully (subject to the <see cref="Mask"/>). Ramp it to fade a layer in and out.</summary>
        public float Weight
        {
            get => _weight;
            set => _weight = Math.Clamp(value, 0f, 1f);
        }
        float _weight = 1f;

        /// <summary>Per-node spatial gate, or null for the whole skeleton. A node's effective contribution is
        /// <c>Weight * Mask.Weight(node)</c>.</summary>
        public BoneMask? Mask { get; set; }

        /// <summary>Override or Additive composition (see <see cref="LayerMode"/>).</summary>
        public LayerMode Mode { get; set; }

        public AnimationLayer(AnimationClip clip, LayerMode mode = LayerMode.Override, BoneMask? mask = null, float weight = 1f, float speed = 1f)
        {
            Clip = clip ?? throw new ArgumentNullException(nameof(clip));
            Mode = mode;
            Mask = mask;
            Weight = weight;
            Speed = speed;
        }

        /// <summary>Swap the clip this layer plays. Resets the playhead to 0 (a fresh action starts at its first
        /// frame) and invalidates the cached additive reference so it re-samples the new clip.</summary>
        public void SetClip(AnimationClip clip)
        {
            Clip = clip ?? throw new ArgumentNullException(nameof(clip));
            Time = 0f;
        }

        /// <summary>Advance this layer's playhead by <c>dt * <see cref="Speed"/></c>, looping within the clip
        /// duration. Steady-state allocation-free.</summary>
        public void Update(float dt)
        {
            Time = AnimationSampler.Wrap(Time + dt * Speed, Clip.Duration);
        }

        /// <summary>This layer's sampled local poses at its current <see cref="Time"/>, written into
        /// <paramref name="into"/> (length must equal the skeleton node count). Steady-state allocation-free.</summary>
        internal void SampleInto(Skeleton skel, JointPose[] into) => AnimationSampler.SampleInto(Clip, skel, Time, into);

        /// <summary>The additive reference pose (this layer's clip sampled at time 0), cached across frames. Sampled
        /// lazily the first time an additive layer needs it and re-sampled only when the clip changes, so the steady
        /// state allocates nothing.</summary>
        internal JointPose[] ReferencePose(Skeleton skel)
        {
            if (_referencePose is null || !ReferenceEquals(_referenceClip, Clip) || _referencePose.Length != skel.NodeCount)
            {
                _referencePose = AnimationSampler.SamplePose(Clip, skel, 0f);
                _referenceClip = Clip;
            }
            return _referencePose;
        }
    }
}
