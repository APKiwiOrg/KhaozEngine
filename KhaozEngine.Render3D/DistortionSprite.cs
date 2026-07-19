using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The screen-space offset field a <see cref="DistortionSprite"/> writes. Each shape is evaluated procedurally
    /// in the distortion fragment shader (SDF/noise, no texture), producing a signed 2D offset the post apply pass
    /// re-samples the resolved scene colour through, so refraction reads as an in-scene phenomenon that warps the
    /// pixels behind it (heat haze, shockwave rings, splash lensing).
    /// </summary>
    public enum DistortionShape : byte
    {
        /// <summary>Radial ring of outward offsets for shockwaves. <see cref="DistortionSprite.ShapeParam"/> sets
        /// the ring band thickness (0 tight, 1 fat).</summary>
        Ripple = 0,
        /// <summary>Upward-scrolling value-noise wobble over the sprite footprint (heat haze).
        /// <see cref="DistortionSprite.ShapeParam"/> sets the noise frequency. The second octave is dropped under
        /// the reduced distortion-quality tier.</summary>
        Heat = 1,
        /// <summary>Smooth radial bulge for splash lensing. A positive <see cref="DistortionSprite.Strength"/> pulls
        /// pixels inward (magnify), a negative one pushes them outward (pinch).
        /// <see cref="DistortionSprite.ShapeParam"/> softens the falloff shoulder.</summary>
        Lens = 2,
    }

    /// <summary>Quality tier for the screen-space distortion pass, mirroring <see cref="ParticleQuality"/>. Reduced
    /// drops the second noise octave in <see cref="DistortionShape.Heat"/> (a uniform shader branch) and renders the
    /// offset field at quarter resolution instead of half.</summary>
    public enum DistortionQuality
    {
        /// <summary>Both heat noise octaves, half-res offset field.</summary>
        Full = 0,
        /// <summary>Single heat noise octave, quarter-res offset field. For weak GPUs.</summary>
        Reduced = 1,
    }

    /// <summary>
    /// One screen-space distortion sprite as the renderer sees it. Queue with
    /// <see cref="Scene3D.DrawDistortion(in DistortionSprite)"/>. The whole queue accumulates into a lazily
    /// allocated half-res offset field with additive blend (overlapping fields sum), depth-occluded against the
    /// scene like the modern particle pass, and the post chain's FIRST pass re-samples the resolved scene colour
    /// through that field so warps precede every camera-response pass (bloom halos follow the warped sources, the
    /// retro path quantizes the warped image). A frame that queues no distortion sprite allocates nothing and
    /// renders byte-identically to before distortion existed. Distortion is presentation-only, gated by
    /// <see cref="Scene3D.DistortionQuality"/>.
    /// </summary>
    public struct DistortionSprite
    {
        /// <summary>World center of the sprite.</summary>
        public Vector3 Position;
        /// <summary>Half-size in world units (matches the particle-sprite size convention).</summary>
        public float Size;
        /// <summary>Roll around the view axis in radians (camera-facing), or in the ground plane for
        /// <see cref="ParticleOrientation.FlatGround"/>.</summary>
        public float Rotation;
        /// <summary>Which offset field this sprite writes.</summary>
        public DistortionShape Shape;
        /// <summary>Per-shape tuning knob in [0,1]. 0 is each shape's documented default look (ripple band width,
        /// heat frequency, lens falloff softness).</summary>
        public float ShapeParam;
        /// <summary>Offset magnitude in world-ish units, converted to a UV excursion by the apply pass's texel
        /// scale (clamped there to a small maximum). For <see cref="DistortionShape.Lens"/> the sign chooses
        /// magnify (positive) or pinch (negative). A value of 0 makes the sprite contribute nothing.</summary>
        public float Strength;
        /// <summary>Age over lifetime in [0,1]. Reserved for life-driven shape terms. Pass 0 when unknown.</summary>
        public float LifeNorm;
        /// <summary>Per-sprite random constant in [0,1) giving noise variety. Pass 0 when unknown.</summary>
        public float Seed;
        /// <summary>Camera-facing (default) or flat in the ground plane (shockwave rings, ground ripples).</summary>
        public ParticleOrientation Orientation;
        /// <summary>Per-sprite multiplier on <see cref="Scene3D.ParticleSoftFade"/> for the depth occlusion. 0
        /// means 1 (the default), matching <see cref="ParticleSprite.SoftFadeScale"/>. Ignored for
        /// <see cref="ParticleOrientation.FlatGround"/> sprites, which skip the depth occlusion entirely (it would
        /// erase a refraction ring lying coplanar with the floor, the same reason the particle pass skips it).</summary>
        public float SoftFadeScale;
    }
}
