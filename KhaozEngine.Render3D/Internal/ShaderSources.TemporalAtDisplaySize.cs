namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE SPLIT'S SECOND PASS AT THE DISPLAY'S OWN SIZE (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 26). Where the
    /// internal size is the display's, the two Lanczos kernels are one and no pixel takes the display-sized
    /// reconstruction (<c>temporalDisplayShare</c> is none wherever <c>Jitter.z</c> is not above 1). Its sum over the
    /// 3x3 and the share still cost the pass. <see cref="TemporalAtDisplaySizeGlsl"/>, placed after the precision
    /// header, compiles <see cref="TemporalAccumulateGlsl"/> without them, so one source keeps every rule and the
    /// program writes the same history as the upscaling one there. <c>TemporalSplitResolve</c> records it on a frame
    /// whose uniforms say the display is no larger than the internal size, the same test the shader makes. The fused
    /// resolve, the debug views and the count probe keep the one program.
    /// </summary>
    internal static partial class ShaderSources
    {
        /// <summary>The switch, right after the precision header.</summary>
        internal const string TemporalAtDisplaySizeGlsl = "#define TemporalAtDisplaySize\n";

        /// <summary>The split's second pass at the display's own size, at full precision.</summary>
        public const string TemporalAccumulateAtDisplaySizeFrag = "#version 450\n" + TemporalFullPrecisionGlsl
            + TemporalAtDisplaySizeGlsl + TemporalAccumulateBodyGlsl;

        /// <summary>The split's second pass at the display's own size, at half precision.</summary>
        public const string TemporalAccumulateAtDisplaySizeHalfFrag = "#version 450\n" + TemporalHalfPrecisionGlsl
            + TemporalAtDisplaySizeGlsl + TemporalAccumulateBodyGlsl;

        /// <summary>The split's second pass at a precision, for a frame that upscales or for one at the display's own
        /// size.</summary>
        public static string TemporalAccumulateFragment(TemporalResolvePrecision precision, bool upscales) =>
            upscales ? TemporalAccumulateFragment(precision)
            : precision == TemporalResolvePrecision.Half ? TemporalAccumulateAtDisplaySizeHalfFrag
            : TemporalAccumulateAtDisplaySizeFrag;
    }
}
