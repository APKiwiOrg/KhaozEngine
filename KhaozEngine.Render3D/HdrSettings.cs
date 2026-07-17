namespace KhaozEngine.Render3D
{
    /// <summary>Tonemap curve for the HDR post chain (see <see cref="HdrSettings"/>).</summary>
    public enum TonemapOperator
    {
        /// <summary>ACES filmic fit (Krzysztof Narkowicz 2015). A filmic S-curve where hot cores desaturate toward
        /// white instead of clipping to a flat primary. The default: it keeps over-range highlights readable and
        /// gives the semi-realistic "A"-tier look this pipeline shipped for.</summary>
        AcesFilmic = 0,
        /// <summary>Classic Reinhard (<c>c / (1 + c)</c>). Softer, flatter highlights than the filmic curve, and it
        /// never fully reaches white. A debugging or stylistic alternative when the ACES roll-off is too aggressive.</summary>
        Reinhard = 1,
        /// <summary>Straight clamp to <c>[0,1]</c>. Applies exposure then hard-clips, showing the raw over-range
        /// clipping a plain LDR pipeline would produce. Useful as an A/B reference for what the tonemap is buying.</summary>
        Clamp = 2,
    }

    /// <summary>
    /// HDR post-chain settings: render the internal colour targets at <c>R16G16B16A16Float</c> so shading can carry
    /// values above 1.0, bloom the over-range highlights BEFORE tonemapping, then map the float scene back to LDR
    /// <c>[0,1]</c> with a filmic <see cref="TonemapOperator"/> ahead of the retro/AA passes and the swapchain blit.
    /// Reachable as <see cref="PixelPostProcessSettings.Hdr"/>. Follows the <see cref="BloomSettings"/> /
    /// <see cref="SkySettings"/> precedent of a plain settings bag with sensible defaults.
    /// <para>
    /// Unlike <see cref="BloomSettings"/>, this is ON by default (<see cref="Enabled"/> == true): the float colour
    /// chain plus ACES tonemap is the standard look. Set <c>Hdr.Enabled = false</c> to restore the exact legacy chain
    /// (UNorm targets, no tonemap, the historical Quantize -> Outline -> Bloom -> FXAA pass order), byte-identical to
    /// the pre-HDR output for retro/pixel-palette games that depend on the quantized result.
    /// </para>
    /// <para>
    /// There is no separate "intensity above 1.0" authoring field on materials, particles, or beams: the engine's
    /// existing unclamped <see cref="KhaozEngine.Primitives.Color"/> IS the HDR authoring surface, so a
    /// <c>new Color(6f, 6f, 6f)</c> emissive simply carries six units of energy through the float chain and blooms /
    /// tonemaps accordingly. The tonemap runs on the engine's existing display-referred shading values (no separate
    /// linear-light conversion pass), which preserves the current art direction while adding headroom.
    /// </para>
    /// </summary>
    public sealed class HdrSettings
    {
        /// <summary>Run the HDR float colour chain + tonemap. Default <c>true</c> (the float16 targets, pre-tonemap
        /// bloom, and ACES tonemap are the standard path). Set <c>false</c> to restore the legacy UNorm chain and pass
        /// order exactly, byte-identical to the pre-HDR output.</summary>
        public bool Enabled = true;

        /// <summary>Linear exposure multiplier applied to the scene colour BEFORE the tonemap operator. Default
        /// <c>1.0</c> (no change). Above 1 pushes more of the scene into the highlight roll-off (brighter, hotter
        /// cores), below 1 pulls it back. Clamped to non-negative at upload time. Ignored when <see cref="Enabled"/>
        /// is <c>false</c>.</summary>
        public float Exposure = 1f;

        /// <summary>Which tonemap curve maps the float scene back to LDR. Default <see cref="TonemapOperator.AcesFilmic"/>
        /// (see <see cref="TonemapOperator"/> for the alternatives and why ACES is the default). Ignored when
        /// <see cref="Enabled"/> is <c>false</c>.</summary>
        public TonemapOperator Operator = TonemapOperator.AcesFilmic;

        /// <summary>
        /// How much of a highlight's colour (hue + saturation) the tonemap preserves as it rolls off, in <c>[0,1]</c>
        /// (clamped at upload). Blends between two ways of compressing an over-range colour to LDR:
        /// <list type="bullet">
        /// <item><description><c>0</c> (default) applies the <see cref="Operator"/> to each channel independently: the
        /// historical look, where a hot core desaturates toward white as its brightest channel saturates first (an
        /// additive glow bleaches out at the top end).</description></item>
        /// <item><description><c>1</c> applies the operator to luminance only and rescales RGB by the mapped
        /// luminance, so only brightness rolls off and the hue is fully preserved (a coloured glow stays chromatic
        /// into its core).</description></item>
        /// </list>
        /// Values in between blend the two. At <c>0</c> the shader short-circuits to the exact per-channel expression,
        /// so the default output is byte-identical to the pre-chroma tonemap. Applies to all three operators. Ignored
        /// when <see cref="Enabled"/> is <c>false</c>.
        /// </summary>
        public float ChromaPreservation = 0f;
    }
}
