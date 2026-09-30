namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE ACCUMULATION'S PRECISION (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3, The resolve). Every program that
    /// includes <see cref="TemporalAccumulateGlsl"/> starts with one of two headers. They name the types of the 3x3's
    /// Lanczos kernels and their products, its colour and alpha range, its sample weight, its largest reactive
    /// difference and its lumas (<c>afloat</c>, <c>avec3</c>, <c>avec4</c>), and the conversions to and from them.
    /// <see cref="TemporalFullPrecisionGlsl"/> makes them single floats and every conversion nothing, so the full
    /// programs compile to the same bytes as before the choice existed. <see cref="TemporalHalfPrecisionGlsl"/> makes
    /// them half floats through <c>GL_EXT_shader_explicit_arithmetic_types_float16</c>.
    /// <para><b>WHAT RUNS AT HALF</b> was chosen by measurement. The prepared values the 3x3 gathers are half floats
    /// already (<c>temporalHalf</c>), so their range, their lumas and their largest reactive difference are exact at
    /// half, and only the kernels and their products round. The reconstruction's sums stay single floats: summed at
    /// half, the followed keyed box at 2 px a frame at Quality left one pixel more of trail than its bound. The
    /// moments, the ridge that refreshes the lock, the clip, the depth tests, the history and the blend stay single
    /// floats too.</para>
    /// <para><b>ONE PRECISION A DEVICE.</b> Every temporal program on a device takes the precision
    /// <see cref="TemporalResolvePrecisionPolicy"/> picks for it: both entry points, so they still agree bit for bit,
    /// and the debug views and the count probe, which re-evaluate the fused core.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- The two headers, each right after #version ----
        internal const string TemporalFullPrecisionGlsl = @"#define afloat float
#define avec3 vec3
#define avec4 vec4
#define toAfloat(v) (v)
#define toAvec3(v) (v)
#define toAvec4(v) (v)
#define toFloat(v) (v)
#define toVec3(v) (v)
// Below every value the 3x3 gathers, and above it: the start of its range.
#define TemporalRangeLimit 1.0e30
";

        internal const string TemporalHalfPrecisionGlsl =
            "#extension GL_EXT_shader_explicit_arithmetic_types_float16 : require\n" + @"#define afloat float16_t
#define avec3 f16vec3
#define avec4 f16vec4
#define toAfloat(v) float16_t(v)
#define toAvec3(v) f16vec3(v)
#define toAvec4(v) f16vec4(v)
#define toFloat(v) float(v)
#define toVec3(v) vec3(v)
// The largest finite half float, which every value the 3x3 gathers lies within.
#define TemporalRangeLimit 65504.0
";

        // ---- The four programs at half precision ----
        public const string TemporalResolveHalfFrag = "#version 450\n" + TemporalHalfPrecisionGlsl
            + TemporalResolveCoreGlsl + TemporalResolveMainGlsl;

        public const string TemporalAccumulateHalfFrag = "#version 450\n" + TemporalHalfPrecisionGlsl
            + TemporalAccumulateBodyGlsl;

        public const string TemporalDebugHalfFrag = "#version 450\n" + TemporalHalfPrecisionGlsl
            + TemporalResolveCoreGlsl + TemporalDebugMainGlsl;

        public const string TemporalProbeHalfFrag = "#version 450\n" + TemporalHalfPrecisionGlsl
            + TemporalResolveCoreGlsl + TemporalProbeMainGlsl;

        /// <summary>The fused resolve at a precision.</summary>
        public static string TemporalResolveFragment(TemporalResolvePrecision precision) =>
            precision == TemporalResolvePrecision.Half ? TemporalResolveHalfFrag : TemporalResolveFrag;

        /// <summary>The split's second pass at a precision. Its first pass has one.</summary>
        public static string TemporalAccumulateFragment(TemporalResolvePrecision precision) =>
            precision == TemporalResolvePrecision.Half ? TemporalAccumulateHalfFrag : TemporalAccumulateFrag;

        /// <summary>The debug views at a precision.</summary>
        public static string TemporalDebugFragment(TemporalResolvePrecision precision) =>
            precision == TemporalResolvePrecision.Half ? TemporalDebugHalfFrag : TemporalDebugFrag;

        /// <summary>The count probe at a precision.</summary>
        public static string TemporalProbeFragment(TemporalResolvePrecision precision) =>
            precision == TemporalResolvePrecision.Half ? TemporalProbeHalfFrag : TemporalProbeFrag;
    }
}
