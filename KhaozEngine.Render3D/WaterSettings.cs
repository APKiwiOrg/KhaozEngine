using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Opt-in animated water surface: a flat plane with a domain-warped three-layer procedural normal field, a
    /// camera-distance detail fade, a depth-driven shallow-water body blend, a fresnel-style blend between the body
    /// tint and a sky-derived horizon tint, a key-light specular glint, and a depth-sampled shore fade at the
    /// waterline. Reachable as <see cref="PixelPostProcessSettings.Water"/> - the same home as
    /// <see cref="PixelPostProcessSettings.Sky"/> (both are scene-wide look-and-feel bags reached off <c>Post</c>,
    /// as opposed to <see cref="WaterPlane"/>, which is the per-frame WHERE-to-draw request via
    /// <see cref="Scene3D.DrawWater(in WaterPlane)"/>). Drawing nothing (no <see cref="Scene3D.DrawWater(in WaterPlane)"/>
    /// call this frame) means no water pass runs at all, regardless of these settings - existing scenes stay
    /// byte-stable. No reflections/probes (roadmap gap #9); this is an LDR stylized surface, not a physically
    /// accurate one.
    /// <para>
    /// The pre-14.22.0 look is reachable by turning the three additions off:
    /// <c>WaveWarpStrength = 0</c>, <c>DetailFadeDistance = 0</c>, <c>ShallowDepth = 0</c>. That restores the old
    /// unwarped, distance-flat, single-body-colour surface. It does NOT restore the old two-octave wave FIELD: those
    /// two octaves were axis-aligned and separable, and that is precisely the checkerboard tiling this release
    /// replaced, so the three-layer field is unconditional.
    /// </para>
    /// </summary>
    public sealed class WaterSettings
    {
        /// <summary>Tint colour in deep water (view ray steep, far from shore). Default a deep teal-blue that
        /// reads as water under the engine's default lighting.</summary>
        public Color DeepColor = new(0.05f, 0.18f, 0.28f, 0.92f);

        /// <summary>Tint colour in SHALLOW water: the body colour the surface blends toward as the ground rises to
        /// meet it (over <see cref="ShallowDepth"/>), so a coastal shelf reads as a lighter, greener fringe instead
        /// of the same deep tint right up to a hard waterline. Applied to the BODY colour, before the fresnel blend
        /// toward <see cref="HorizonColor"/>, so a grazing view of the shallows still picks up the sky. Default a
        /// modest lift off <see cref="DeepColor"/> (lighter, slightly greener, a touch more transparent) rather than
        /// a tropical lagoon: it is meant to read as the same water getting shallow.</summary>
        public Color ShallowColor = new(0.14f, 0.34f, 0.38f, 0.80f);

        /// <summary>World-space depth below the surface over which <see cref="ShallowColor"/> blends into
        /// <see cref="DeepColor"/>: full shallow tint where the ground touches the surface, full deep tint at this
        /// depth and beyond. Independent of <see cref="ShoreFadeDistance"/> (which is the much tighter ALPHA
        /// feather at the waterline itself), because a shallows tint reads over metres while the edge feather is
        /// centimetres. <c>0</c> or less disables the shallow blend entirely (the pre-14.22.0 look: one body colour
        /// at every depth). Default <c>2.5</c>.</summary>
        public float ShallowDepth = 2.5f;

        /// <summary>Tint colour toward the horizon (grazing view angle): blended in via the fresnel term so the
        /// surface reflects the sky colour at shallow viewing angles, over whichever body colour
        /// <see cref="DeepColor"/>/<see cref="ShallowColor"/> resolved to. Default close to
        /// <see cref="SkySettings.HorizonColor"/>'s default so an enabled sky and enabled water read as one
        /// cohesive scene without the game having to hand-match colours; override to re-harmonize with a custom
        /// sky palette.</summary>
        public Color HorizonColor = new(0.62f, 0.70f, 0.80f, 0.75f);

        /// <summary>World-space size of one wave layer's tiling (larger = broader, slower-looking swell). Sets the
        /// wavelength of the broad base layer; the two finer layers derive theirs from it via fixed irrational
        /// multipliers. Default <c>2.5</c>.</summary>
        public float WaveScale = 2.5f;

        /// <summary>How fast the scrolling wave layers animate (world units / second-ish; drives the
        /// <see cref="Scene3D.EffectTimeSeconds"/>-scaled scroll). Default <c>0.35</c>.</summary>
        public float WaveSpeed = 0.35f;

        /// <summary>Strength of the procedural normal perturbation (0 = perfectly flat/mirror-like, larger =
        /// choppier-looking ripples). Default <c>0.35</c>.</summary>
        public float NormalStrength = 0.35f;

        /// <summary>How far a slow, large-scale domain warp displaces the wave sample position before the three
        /// wave layers are evaluated, in multiples of <see cref="WaveScale"/>. The warp's own wavelength is roughly
        /// five times the base layer's, so it bends the wave field over a much longer distance than the waves
        /// themselves repeat over, which is what stops a large surface reading as a repeating grid. <c>0</c>
        /// disables the warp (the wave layers are then sampled at the raw world position). Default
        /// <c>0.75</c>.</summary>
        public float WaveWarpStrength = 0.75f;

        /// <summary>Camera distance (world units) over which the two FINE wave layers fade out toward
        /// <see cref="DistantDetailScale"/>, leaving only the broad base swell in the far field. This is the
        /// anti-shimmer knob: high-frequency normals sampled below a pixel alias into a crawling moire, and a tight
        /// <see cref="GlintExponent"/> turns that into sparkle. <c>0</c> or less disables the fade, so the fine
        /// layers run at full strength to the horizon (the pre-14.22.0 behaviour). Default <c>60</c>.</summary>
        public float DetailFadeDistance = 60f;

        /// <summary>Fraction of the fine wave layers that survives at and beyond <see cref="DetailFadeDistance"/>
        /// (clamped to 0..1). <c>0</c> leaves the far field as the base swell alone (glassiest, least shimmer);
        /// <c>1</c> is equivalent to no fade at all. Ignored when <see cref="DetailFadeDistance"/> is disabled.
        /// Default <c>0.18</c>.</summary>
        public float DistantDetailScale = 0.18f;

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
