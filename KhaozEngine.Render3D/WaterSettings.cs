using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Opt-in animated water surface: a flat plane with procedural normal perturbation, a fresnel-style blend
    /// between a deep tint and a sky-derived horizon tint, a key-light specular glint, and depth-sampled shore
    /// fade. Reachable as <see cref="PixelPostProcessSettings.Water"/> - the same home as
    /// <see cref="PixelPostProcessSettings.Sky"/> (both are scene-wide look-and-feel bags reached off <c>Post</c>,
    /// as opposed to <see cref="WaterPlane"/>, which is the per-frame WHERE-to-draw request via
    /// <see cref="Scene3D.DrawWater(in WaterPlane)"/>). Drawing nothing (no <see cref="Scene3D.DrawWater(in WaterPlane)"/>
    /// call this frame) means no water pass runs at all, regardless of these settings - existing scenes stay
    /// byte-stable. No reflections/probes (roadmap gap #9); this is an LDR stylized surface, not a physically
    /// accurate one.
    /// </summary>
    public sealed class WaterSettings
    {
        /// <summary>Tint colour in deep water (view ray steep, far from shore). Default a deep teal-blue that
        /// reads as water under the engine's default lighting.</summary>
        public Color DeepColor = new(0.05f, 0.18f, 0.28f, 0.92f);

        /// <summary>Tint colour toward the horizon (grazing view angle): blended in via the fresnel term so the
        /// surface reflects the sky colour at shallow viewing angles. Default close to
        /// <see cref="SkySettings.HorizonColor"/>'s default so an enabled sky and enabled water read as one
        /// cohesive scene without the game having to hand-match colours; override to re-harmonize with a custom
        /// sky palette.</summary>
        public Color HorizonColor = new(0.62f, 0.70f, 0.80f, 0.75f);

        /// <summary>World-space size of one wave octave's tiling (larger = broader, slower-looking swell).
        /// Default <c>2.5</c>.</summary>
        public float WaveScale = 2.5f;

        /// <summary>How fast the two scrolling wave octaves animate (world units / second-ish; drives the
        /// <see cref="Scene3D.EffectTimeSeconds"/>-scaled scroll). Default <c>0.35</c>.</summary>
        public float WaveSpeed = 0.35f;

        /// <summary>Strength of the procedural normal perturbation (0 = perfectly flat/mirror-like, larger =
        /// choppier-looking ripples). Default <c>0.35</c>.</summary>
        public float NormalStrength = 0.35f;

        /// <summary>World-space distance over which the surface fades out near the shore (where the resolved
        /// scene depth shows the ground is close beneath the water), softening the waterline instead of a hard
        /// clip. Default <c>0.6</c>.</summary>
        public float ShoreFadeDistance = 0.6f;

        /// <summary>Strength of the key-light specular sun glint (Blinn-Phong-style, water-specific exponent).
        /// 0 disables the glint entirely. Default <c>0.6</c>.</summary>
        public float GlintStrength = 0.6f;

        /// <summary>Specular exponent (tightness) of the sun glint: higher = a smaller, sharper highlight.
        /// Default <c>140</c> (tighter than the model pass's typical shininess - water glints are small and
        /// bright, not a broad soft highlight).</summary>
        public float GlintExponent = 140f;

        /// <summary>Overall opacity multiplier applied on top of <see cref="DeepColor"/>/<see cref="HorizonColor"/>'s
        /// own alpha (0 = invisible, 1 = full). Default <c>1</c>.</summary>
        public float Opacity = 1f;
    }
}
