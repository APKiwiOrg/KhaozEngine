using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Procedural sprite shape evaluated in the particle fragment shader (SDF/noise, no texture). The engine is
    /// procedural-first: these cover the modern effect vocabulary (glows, embers, streaks, smoke, shockwaves,
    /// glints) without an atlas pipeline. Artist-authored textures remain available through the textured
    /// <see cref="Scene3D.DrawBillboard(Scene3D.TextureHandle, Vector3, float, Vector4, Color, BillboardBlend)"/> path.
    /// </summary>
    public enum ParticleShape : byte
    {
        /// <summary>Soft gaussian-like disc. Param 0 reads like the classic soft blob, param toward 1 tightens
        /// the falloff to a hotter center.</summary>
        SoftGlow = 0,
        /// <summary>Tight hot core plus a faint warm halo, with a subtle per-particle flicker. Param widens the
        /// core (0 tight ember, 1 fat coal).</summary>
        Ember = 1,
        /// <summary>Rounded streak along the sprite's local X axis with a bright head and tapered tail. Pairs
        /// with <see cref="ParticleSprite.Stretch"/> so the streak follows on-screen motion. Param sharpens the
        /// head-to-tail ramp.</summary>
        Spark = 2,
        /// <summary>Noise-eroded smoke wisp: the erosion threshold rises with <see cref="ParticleSprite.LifeNorm"/>
        /// so the sprite dissolves at its edges instead of fading uniformly. Param biases the erosion (0 denser,
        /// 1 wispier).</summary>
        Wisp = 3,
        /// <summary>Soft annulus for shockwaves and impact rings. Param widens the band.</summary>
        Ring = 4,
        /// <summary>Four-point glint for sparkles and magic motes. Param sharpens the rays.</summary>
        Star = 5,
    }

    /// <summary>How a <see cref="ParticleSprite"/>'s quad is oriented in the world.</summary>
    public enum ParticleOrientation : byte
    {
        /// <summary>Billboard toward the camera (the default for glows, sparks, smoke).</summary>
        CameraFacing = 0,
        /// <summary>Lie flat in the ground plane (XZ), for shockwave rings and ground glows. Pair with a small
        /// <see cref="ParticleSprite.SoftFadeScale"/> so the floor immediately behind the quad does not erase
        /// it, and lift the sprite slightly above the surface.</summary>
        FlatGround = 1,
    }

    /// <summary>Quality tier for the particle pass, mirroring <see cref="GroundDecalQuality"/>. Reduced drops the
    /// second noise octave and the ember flicker (a uniform branch in the shader, not a pipeline variant).</summary>
    public enum ParticleQuality
    {
        /// <summary>Both noise octaves and the ember flicker.</summary>
        Full = 0,
        /// <summary>Single noise octave, no flicker. For weak GPUs.</summary>
        Reduced = 1,
    }

    /// <summary>
    /// One particle as the renderer sees it: a camera-facing procedural sprite. Queue with
    /// <see cref="Scene3D.DrawParticle(in ParticleSprite)"/> / <see cref="Scene3D.DrawParticles(System.ReadOnlySpan{ParticleSprite})"/>.
    /// The whole queue renders as ONE premultiplied-alpha instanced draw, sorted back-to-front, depth-tested
    /// against the scene (no depth write) and soft-faded near geometry (see <see cref="Scene3D.ParticleSoftFade"/>),
    /// BEFORE the post chain, so additive sprites feed bloom and every sprite participates in the pixel post like
    /// meshes do. This is the modern path. The untextured <see cref="Scene3D.DrawBillboard(Vector3, float, Color, BillboardBlend)"/>
    /// overlay remains the legacy path: post-post, unoccluded, always crisp.
    /// </summary>
    public struct ParticleSprite
    {
        /// <summary>World center of the sprite.</summary>
        public Vector3 Position;
        /// <summary>World velocity, used only when <see cref="Stretch"/> is positive: the quad elongates along
        /// the on-screen projection of this vector.</summary>
        public Vector3 Velocity;
        /// <summary>Half-size in world units (matches the legacy billboard size convention).</summary>
        public float Size;
        /// <summary>Roll around the view axis in radians. Ignored while the sprite is velocity-stretched.</summary>
        public float Rotation;
        /// <summary>Sprite tint. Alpha scales the shape's own coverage.</summary>
        public Color Color;
        /// <summary>Procedural shape evaluated in the fragment shader.</summary>
        public ParticleShape Shape;
        /// <summary>Per-shape tuning knob in [0,1]. 0 is each shape's documented default look.</summary>
        public float ShapeParam;
        /// <summary>Age over lifetime in [0,1]. Drives the wisp erosion. Pass 0 when unknown.</summary>
        public float LifeNorm;
        /// <summary>Per-particle random constant in [0,1) giving noise variety. Pass 0 when unknown.</summary>
        public float Seed;
        /// <summary>Velocity-stretch factor: 0 keeps the round camera-facing quad, larger values elongate the
        /// quad along on-screen motion by roughly <c>1 + Stretch * speed / Size</c> (clamped in the shader).</summary>
        public float Stretch;
        /// <summary>Alpha or additive compositing. Both blend modes share one sorted draw (the shader emits
        /// premultiplied color), so alpha smoke and additive glow interleave correctly.</summary>
        public BillboardBlend Blend;
        /// <summary>Camera-facing (default) or flat in the ground plane (shockwave rings, ground glows).</summary>
        public ParticleOrientation Orientation;
        /// <summary>Per-sprite multiplier on <see cref="Scene3D.ParticleSoftFade"/>. 0 means 1 (the default).
        /// Flat-on-ground sprites want a small value (around 0.1) so the floor just behind them does not fade
        /// them out, and dense smoke can raise it for a longer, softer approach.</summary>
        public float SoftFadeScale;
    }
}
