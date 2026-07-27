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
        /// <summary>Lie flat in the ground plane (XZ), for shockwave rings and ground glows. The soft depth fade
        /// (see <see cref="Scene3D.ParticleSoftFade"/>) is SKIPPED for this orientation: a quad lying in the ground
        /// plane is coplanar with the very floor the fade measures against, and at a grazing camera angle that
        /// erases the ring's near/far arcs. Just lift the sprite slightly above the surface so it wins the depth
        /// test against the floor. <see cref="ParticleSprite.SoftFadeScale"/> is therefore ignored here.</summary>
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
    /// Authored-atlas playback spec for a <see cref="ParticleSprite"/>: a flipbook sheet (a grid of
    /// <see cref="Columns"/> x <see cref="Rows"/> frames packed into one texture) sampled per frame in the particle
    /// fragment shader, with optional motion-vector frame interpolation. The default (an invalid <see cref="Texture"/>
    /// handle) leaves the sprite on the procedural <see cref="ParticleShape"/> path, so a sprite that never sets this
    /// renders byte-identically to before flipbooks existed. Load the atlas (and the optional motion sheet) with
    /// <see cref="Scene3D.LoadTexture(byte[],int,int)"/>.
    /// <para>
    /// UV ORIGIN: a flipbook cell samples with its origin at the BOTTOM-LEFT, matching the rest of the 3D sprite
    /// path (see the <c>(u0,v0)</c> note on
    /// <see cref="BillboardGeometry.Triangles(System.Numerics.Vector3,float,System.Numerics.Vector3,System.Numerics.Vector3,System.Numerics.Vector4,System.Span{System.Numerics.Vector3},System.Span{System.Numerics.Vector2})"/>).
    /// The 2D <c>SpriteBatch</c> path samples the SAME image file top-left. So an atlas packed by a top-left tool
    /// (PIL, and most sprite packers) renders through this path with every cell VERTICALLY INVERTED. Set
    /// <see cref="FlipV"/> to true to correct it. That is the fix for the "my flipbook plays upside down" symptom,
    /// and it is per-spec, so a bottom-left-authored sheet keeps the default.
    /// </para>
    /// </summary>
    /// <param name="Texture">The atlas sheet, a grid of frame cells. An invalid handle keeps the sprite procedural.</param>
    /// <param name="Columns">Atlas columns (frames per row). Must be positive for the spec to be active, and is
    /// clamped to 127 by the GPU packing (a 128-column sheet at even a 64px cell is 8192px wide, at or past the max
    /// texture size on most GPUs, so the cap is unreachable in practice).</param>
    /// <param name="Rows">Atlas rows. Must be positive for the spec to be active, and is clamped to 127 by the GPU
    /// packing for the same reason as <paramref name="Columns"/>.</param>
    /// <param name="MotionTexture">Optional motion-vector sheet on the same grid, driving the two-tap frame warp. An
    /// absent or neutral sheet degrades to a plain cross-fade, so no flag is needed to opt out of the warp.</param>
    /// <param name="MotionStrength">Scales the motion-vector displacement, clamped to [0,4] and quantized to 1/64. 1 is
    /// the authored strength, 0 disables the warp (plain cross-fade).</param>
    /// <param name="Loop">When true the frame past the last wraps back to the first (looping fire/smoke). When false
    /// playback clamps on the last frame (one-shot explosion sheets).</param>
    /// <param name="FlipU">Mirrors each cell horizontally. Only the coordinate WITHIN a cell flips, the cell the
    /// frame index selects does not move, so playback order is untouched.</param>
    /// <param name="FlipV">Mirrors each cell vertically. This is the one most consumers need: it makes a
    /// TOP-LEFT-authored atlas (PIL and most packers) sample correctly through this bottom-left path. Like
    /// <paramref name="FlipU"/> it flips only within a cell, never the cell selection.</param>
    public readonly record struct ParticleFlipbook(
        Scene3D.TextureHandle Texture,
        int Columns,
        int Rows,
        Scene3D.TextureHandle MotionTexture = default,
        float MotionStrength = 1f,
        bool Loop = false,
        bool FlipU = false,
        bool FlipV = false)
    {
        /// <summary>True when this spec names a real atlas with a positive grid, so the renderer routes the sprite
        /// through the flipbook path instead of the procedural shapes.</summary>
        public bool IsActive => Texture.IsValid && Columns > 0 && Rows > 0;
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
        /// Dense smoke can raise it for a longer, softer approach. Ignored for
        /// <see cref="ParticleOrientation.FlatGround"/> sprites, which skip the depth fade entirely (it would
        /// erase a quad lying coplanar with the floor).</summary>
        public float SoftFadeScale;
        /// <summary>Optional authored-atlas playback. The default (an invalid <see cref="ParticleFlipbook.Texture"/>)
        /// keeps the sprite on the procedural <see cref="Shape"/> path. When active, the atlas frame selected by
        /// <see cref="FlipbookFrame"/> supplies the sprite's coverage and colour in place of the procedural shape.</summary>
        public ParticleFlipbook Flipbook;
        /// <summary>Continuous flipbook frame position, used only when <see cref="Flipbook"/> is active. The integer
        /// part is the current cell. The fractional part is the blend toward the next cell (motion-vector warped when
        /// a motion sheet is bound, otherwise a straight cross-fade). The frame past the last wraps to the first when
        /// <see cref="ParticleFlipbook.Loop"/> is set, otherwise it clamps on the last frame. Ignored on the
        /// procedural path.</summary>
        public float FlipbookFrame;
    }
}
