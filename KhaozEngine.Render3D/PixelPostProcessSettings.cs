using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How the 3D scene's internal offscreen render target is sized.
    /// </summary>
    public enum RenderScale
    {
        /// <summary>Render at a fixed <see cref="PixelPostProcessSettings.RenderWidth"/> x
        /// <see cref="PixelPostProcessSettings.RenderHeight"/> target, then blit-scale it to the window. This is the
        /// default and the retro/pixel path (a small fixed target + <see cref="PixelPostProcessSettings.Pixelated"/>
        /// gives chunky pixels). On windows larger than the fixed target the final blit UPscales, so the smooth
        /// (non-pixelated) look softens.</summary>
        FixedInternal,
        /// <summary>Size the internal target to the actual framebuffer/viewport each frame (clamped to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/> x <see cref="PixelPostProcessSettings.MaxRenderHeight"/>),
        /// so the final blit is 1:1 (or a downscale at the cap) instead of an upscale. Kills upscale blur on large
        /// windows / zoomed-out views. <see cref="PixelPostProcessSettings.RenderWidth"/>/<c>RenderHeight</c> are
        /// ignored in this mode.</summary>
        MatchViewport,
    }

    /// <summary>
    /// Post-process toggles + parameters. Every stage is independent. Defaults target a smooth, stylized
    /// (non-retro) space look: high internal render resolution, anti-aliased downscale, dark background,
    /// smooth diffuse, no palette/dither/cel. Flip the toggles for the chunky retro/pixel look.
    /// </summary>
    public sealed class PixelPostProcessSettings
    {
        /// <summary>Internal render target sizing. Default <see cref="Render3D.RenderScale.FixedInternal"/> keeps the
        /// historical fixed-resolution path (and the retro look); <see cref="Render3D.RenderScale.MatchViewport"/>
        /// tracks the framebuffer to avoid upscale blur on large windows.</summary>
        public RenderScale RenderScale = RenderScale.FixedInternal;

        /// <summary>Graphics-quality knobs for the scene - today the anti-aliasing selection
        /// (<see cref="Render3D.RenderQuality.AntiAliasing"/>), the recommended high-level way to pick AA
        /// (<c>Quality.AntiAliasing = AntiAliasing.Ssaa(3f)</c>, <c>AntiAliasing.Fxaa</c>, <c>AntiAliasing.Msaa(4)</c>).
        /// Default <see cref="AntiAliasing.Off"/>, so the low-level fields below (<see cref="RenderScale"/> /
        /// <see cref="Supersample"/> / <see cref="Pixelated"/>) still govern and existing scenes are unchanged. When a
        /// non-None mode is set it OVERRIDES the matching low-level fields (SSAA forces MatchViewport + drives
        /// <see cref="Supersample"/>); the raw fields remain the low-level equivalent for back-compat.</summary>
        public RenderQuality Quality = new();

        /// <summary>Procedural sky settings (gradient + optional sun disc). Default disabled, so the background stays
        /// the clear colour + starfield and existing scenes are byte-stable; set <c>Sky.Enabled = true</c> to render a
        /// horizon-&gt;zenith gradient sky behind the geometry, with the sun aligned to <see cref="LightDirection"/>
        /// by default. The cohesive-look pairing for the semi-realistic outdoor preset (pair with
        /// <see cref="UseSmoothPreset"/> and shadows).</summary>
        public SkySettings Sky = new();

        /// <summary>LDR threshold + separable-blur bloom (beams, emissive materials, bright billboards read as a
        /// glow instead of flat). Default disabled, so the post chain runs no extra passes and existing scenes are
        /// byte-stable; set <c>Bloom.Enabled = true</c> to turn it on. See <see cref="BloomSettings"/> for the knobs
        /// and the LDR-not-HDR caveat.</summary>
        public BloomSettings Bloom = new();

        /// <summary>Stylized water surface look-and-feel (body colours + depth absorption, the Gerstner swell,
        /// ripple scale/speed/warp and its distance detail fade, the sky-reflection blend, the GGX glint, foam, and
        /// the shore fade). Lives here alongside <see cref="Sky"/> - whose palette the water REFLECTS - (both are scene-wide appearance bags reached off
        /// <c>Post</c>); WHERE to draw water each frame is the separate per-frame
        /// <see cref="Scene3D.DrawWater(in WaterPlane)"/> request. No request queued this frame means no water pass
        /// runs at all, regardless of these settings - existing scenes stay byte-stable. See
        /// <see cref="WaterSettings"/> for the knobs and the no-reflections caveat.</summary>
        public WaterSettings Water = new();

        /// <summary>HDR float colour chain + filmic tonemap. Default ENABLED (unlike the other post bags): the
        /// internal colour targets render at <c>R16G16B16A16Float</c>, over-range highlights bloom before tonemapping,
        /// and an ACES filmic curve maps the scene back to LDR before the swapchain blit. Set <c>Hdr.Enabled = false</c>
        /// to restore the exact legacy UNorm chain and pass order (byte-identical to the pre-HDR output). See
        /// <see cref="HdrSettings"/> for exposure, the operator choices, and the unclamped-Color authoring note.</summary>
        public HdrSettings Hdr = new();

        /// <summary>Internal render width (used only when <see cref="RenderScale"/> is
        /// <see cref="Render3D.RenderScale.FixedInternal"/>). High = smooth; small + Pixelated = chunky retro pixels.</summary>
        public int RenderWidth = 1600;
        /// <summary>Internal render height (used only when <see cref="RenderScale"/> is
        /// <see cref="Render3D.RenderScale.FixedInternal"/>).</summary>
        public int RenderHeight = 900;

        /// <summary>Upper bound on the internal target width when <see cref="RenderScale"/> is
        /// <see cref="Render3D.RenderScale.MatchViewport"/>, so giant windows don't allocate unbounded targets. The
        /// viewport is scaled down to fit this cap, aspect preserved.</summary>
        public int MaxRenderWidth = 3840;
        /// <summary>Upper bound on the internal target height when <see cref="RenderScale"/> is
        /// <see cref="Render3D.RenderScale.MatchViewport"/> (see <see cref="MaxRenderWidth"/>).</summary>
        public int MaxRenderHeight = 2160;

        /// <summary>Supersampling factor for <see cref="Render3D.RenderScale.MatchViewport"/> (ignored for
        /// FixedInternal). The internal 3D target is rendered at framebuffer x this factor per axis, then downsampled
        /// to the framebuffer by the final blit - anti-aliasing BOTH geometry edges AND shaded texture interiors
        /// (unlike MSAA, which only covers geometry). 1 = off; 2 = 2x per axis (4x the pixels), the same effective AA
        /// a 2x/Retina display gives for free, which fixes high-frequency-terrain / thin-foliage shimmer on a
        /// standard-DPI display. The result is still clamped to <see cref="MaxRenderWidth"/>/<see cref="MaxRenderHeight"/>.
        /// The downscale is a correct mip-filtered (trilinear) box at ANY factor - the internal target carries a mip
        /// chain that the final blit samples at LOD ~= log2(factor) - so 3 and 4 anti-alias properly, not just 2. Cost
        /// scales ~factor^2 in fragment shading (3x = 9x the pixels), so keep it off by default and measure on the
        /// target GPU before going above 2.</summary>
        public float Supersample = 1f;

        /// <summary>Point-sample the final upscale for crisp pixels (retro). False = smooth/AA downscale.</summary>
        public bool Pixelated = false;

        /// <summary>
        /// Opt-in: mip-filter the final blit even under <see cref="Render3D.RenderScale.FixedInternal"/> when the
        /// window is SMALLER than the fixed <see cref="RenderWidth"/> x <see cref="RenderHeight"/> internal target
        /// in either axis (a genuine downscale). Default <c>false</c> keeps the historical single bilinear tap on
        /// that downscale, so every existing consumer and GPU golden stays byte-identical. FixedInternal's fixed
        /// target does not track the window, so a window smaller than it under-samples on that bilinear tap (the
        /// historical Ruinborne shimmer on a window under the 1600x900 default). Turning this on reuses the SAME
        /// mip-chain + trilinear-sampled blit machinery <see cref="Render3D.RenderScale.MatchViewport"/> already
        /// uses for its own supersample downscale (see <see cref="Supersample"/>), so a correct multi-tap box
        /// replaces the single tap at any downscale ratio. Ignored (no effect) under
        /// <see cref="Render3D.RenderScale.MatchViewport"/> (which is unconditionally mip-filtered already) and
        /// under <see cref="Pixelated"/> (the retro path is
        /// always single-mip point-sampled). A consumer that expects a small window should prefer
        /// <see cref="Render3D.RenderScale.MatchViewport"/> outright (it also avoids the fixed target's upscale
        /// blur on large windows). Set this instead only when the FixedInternal retro-sized target itself must be
        /// kept (e.g. a deliberately low, chunky-but-smooth internal resolution) while still supporting windows
        /// smaller than it.
        /// </summary>
        public bool MipFilterFixedInternalDownscale = false;

        /// <summary>Snap each pixel to the nearest color in <see cref="ActivePalette"/> (retro).</summary>
        public bool Quantize = false;
        /// <summary>4x4 Bayer ordered dither applied with quantization (retro).</summary>
        public bool Dither = false;
        /// <summary>Depth/normal discontinuity edge outline (stylized cel look). Off by default engine-wide - the
        /// toon outline is opt-in. Consumers that want it set this true (or drive it from a settings toggle).</summary>
        public bool Outline = false;
        /// <summary>Procedural starfield in the background (assumes a dark space background).</summary>
        public bool Starfield = true;

        /// <summary>
        /// What fills the background, as one mutually-exclusive knob. DERIVED from <see cref="Starfield"/> and
        /// <see cref="SkySettings.Enabled"/> rather than stored, so it can never drift from them: the getter encodes
        /// the engine's long-standing sky-over-starfield-over-solid precedence, and the setter clears the modes it
        /// did not select. Prefer this over setting the booleans directly.
        /// </summary>
        public BackgroundMode Background
        {
            get => Sky.Enabled ? BackgroundMode.Sky
                 : Starfield ? BackgroundMode.Starfield
                 : BackgroundMode.Solid;
            set
            {
                Sky.Enabled = value == BackgroundMode.Sky;
                Starfield = value == BackgroundMode.Starfield;
            }
        }

        /// <summary>0 = smooth diffuse; N&gt;0 = cel shading with N light bands (retro/toon).</summary>
        public int CelBands = 0;

        public Palette ActivePalette = Palettes.Ember8;
        public Color OutlineColor = new(0.02f, 0.02f, 0.04f, 1f);
        // Clip depth is non-linear (compressed near the far plane); the threshold only needs to catch
        // genuine occlusion jumps. The silhouette rim comes from the normal edge (normal flips vs background).
        public float OutlineDepthThreshold = 0.2f;
        public float OutlineNormalThreshold = 0.45f;

        /// <summary>Fade the edge outline out with distance so far foliage/terrain stops aliasing into mush.
        /// Default OFF (the ortho path and existing look are unchanged). When on, outline strength ramps from
        /// full at <see cref="OutlineFadeStart"/> view-space units to zero at <see cref="OutlineFadeEnd"/>.
        /// Only meaningful under a perspective camera.</summary>
        public bool OutlineDistanceFade = false;
        /// <summary>View-space eye distance where the outline begins to fade (see <see cref="OutlineDistanceFade"/>).</summary>
        public float OutlineFadeStart = 40f;
        /// <summary>View-space eye distance where the outline has fully faded (see <see cref="OutlineDistanceFade"/>).</summary>
        public float OutlineFadeEnd = 120f;

        /// <summary>Scene background (cleared behind the model). Dark = space.</summary>
        public Color BackgroundColor = new(0.02f, 0.03f, 0.06f, 1f);

        /// <summary>
        /// Emit the background as transparent (alpha 0) in the final image instead of opaque, so the scene can be
        /// composited over something else - e.g. an offscreen model preview drawn into a 2D panel (see
        /// <see cref="Render3DPreview"/>). The whole stylized chain still runs; only the final blit keeps the
        /// per-pixel alpha (geometry stays opaque, the cleared background stays clear). Default false (the
        /// historical opaque output for an on-screen surface). Has no useful effect together with a non-<see cref="BackgroundMode.Solid"/>
        /// <see cref="Background"/> (the stars and sky fill the background opaquely), so a transparent preview requires
        /// <see cref="Background"/> = <see cref="BackgroundMode.Solid"/>.
        /// </summary>
        public bool TransparentBackground = false;

        /// <summary>Direction the key (sun) light travels (will be normalized).</summary>
        public Vector3 LightDirection = new(-0.5f, -0.85f, -0.35f);
        public Color LightColor = new(1f, 0.95f, 0.86f, 1f);
        public Color AmbientColor = new(0.16f, 0.19f, 0.30f, 1f);

        /// <summary>Direction the fill light travels (will be normalized). Dim cool fill from the other side
        /// so forms don't read flat. Specular comes from the key light only.</summary>
        public Vector3 FillLightDirection = new(0.6f, -0.3f, 0.5f);
        /// <summary>Fill light colour (dim cool by default).</summary>
        public Color FillLightColor = new(0.20f, 0.24f, 0.34f, 1f);

        // ---- Resolved AA config -----------------------------------------------------------------------------------
        // The renderer reads these (never the raw Quality/RenderScale/Supersample directly) so the high-level
        // AntiAliasing selection and the low-level fields resolve in one place. AntiAliasing.None => the raw fields win
        // (back-compat); a non-None mode overrides the matching field; the Pixelated retro path forces AA off.

        /// <summary>The AA mode actually in effect: <see cref="Render3D.AntiAliasingMode.None"/> whenever
        /// <see cref="Pixelated"/> is set (the retro path bypasses AA), else <see cref="Quality"/>'s mode.</summary>
        internal AntiAliasingMode EffectiveAaMode => Pixelated ? AntiAliasingMode.None : Quality.AntiAliasing.Mode;

        /// <summary>Render-target sizing after the AA selection: <see cref="Render3D.AntiAliasingMode.Ssaa"/> forces
        /// <see cref="Render3D.RenderScale.MatchViewport"/> (SSAA supersamples the viewport); otherwise the raw
        /// <see cref="RenderScale"/>.</summary>
        internal RenderScale EffectiveRenderScale =>
            EffectiveAaMode == AntiAliasingMode.Ssaa ? RenderScale.MatchViewport : RenderScale;

        /// <summary>Supersample factor after the AA selection: <see cref="Render3D.AntiAliasingMode.Ssaa"/> uses its
        /// factor (>= 1); otherwise the raw <see cref="Supersample"/> field (so a consumer setting
        /// <see cref="Supersample"/> directly with AA <see cref="AntiAliasing.Off"/> is unchanged).</summary>
        internal float EffectiveSupersample =>
            EffectiveAaMode == AntiAliasingMode.Ssaa ? System.MathF.Max(1f, Quality.AntiAliasing.SsaaFactor) : Supersample;

        /// <summary>Whether the FXAA post pass runs this frame (FXAA alone or opted in after MSAA, and
        /// not <see cref="Pixelated"/>).</summary>
        internal bool EffectiveFxaa => EffectiveAaMode != AntiAliasingMode.None && Quality.AntiAliasing.UsesFxaa;

        /// <summary>Requested MSAA sample count after the AA selection (1 = off). Still clamped to the device maximum
        /// at pipeline-build time via <see cref="AntiAliasing.ResolveFor"/>; this is only the requested value.</summary>
        internal int EffectiveMsaaSamples =>
            EffectiveAaMode == AntiAliasingMode.Msaa ? System.Math.Max(1, Quality.AntiAliasing.MsaaSamples) : 1;

        /// <summary>Dial the stylized post chain down for a smooth/realistic look: cel bands off, palette
        /// quantize + dither off, edge outline off, starfield off, smooth (non-pixelated) upscale. Lighting,
        /// colours, and render scaling are left untouched. Pair with normal/roughness maps (PBR-lite) for a
        /// semi-realistic material - the post chain otherwise still quantizes/outlines a realistic surface.</summary>
        public void UseSmoothPreset()
        {
            CelBands = 0;
            Quantize = false;
            Dither = false;
            Outline = false;
            Starfield = false;
            Pixelated = false;
        }
    }
}
