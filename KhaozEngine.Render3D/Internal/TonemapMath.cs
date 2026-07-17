using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure tonemap math shared between the C# host (headless tests, look-evidence dumps) and the GLSL
    /// <c>TonemapFrag</c>, which mirrors <see cref="Map"/> exactly (keep in sync, like <c>BloomMath</c> mirrors the
    /// bloom shaders and <c>OutlineMath</c> mirrors <c>EdgeFrag</c>). No GPU state, no allocations.
    /// <para>
    /// The mapping compresses an over-range HDR colour to LDR two ways and blends between them by the
    /// <see cref="HdrSettings.ChromaPreservation"/> factor:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Per-channel</b> (factor 0): the selected operator applied independently to R, G, B. The
    /// historical look, where an over-range core desaturates toward white as its brightest channel saturates first.</description></item>
    /// <item><description><b>Hue-preserving</b> (factor 1): the operator applied to luminance only, then RGB rescaled
    /// by <c>mappedLuma / luma</c>, so only brightness rolls off and the chromaticity (hue + saturation direction) is
    /// held. A saturating clamp bounds the result.</description></item>
    /// </list>
    /// <para>
    /// Factor 0 short-circuits to the exact per-channel expression, so the default (0) output is byte-identical to the
    /// pre-chroma tonemap on real hardware (the golden gate proves it). This is the engine's most-shipped pixel.
    /// </para>
    /// </summary>
    internal static class TonemapMath
    {
        /// <summary>Rec. 601 relative luma (the same <c>dot(c, vec3(0.299, 0.587, 0.114))</c> weights the local
        /// <c>luma()</c> in <c>TonemapFrag</c> and the rest of the post chain use).</summary>
        public static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

        /// <summary>ACES filmic fit (Krzysztof Narkowicz 2015), one scalar channel. Mirrors <c>acesFilm</c> in
        /// <c>TonemapFrag</c> exactly (the vec3 form is this applied component-wise).</summary>
        public static float AcesFilm(float x)
            => Math.Clamp((x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);

        /// <summary>The selected tonemap operator applied to one scalar value (<paramref name="op"/> 0 aces,
        /// 1 reinhard, 2 clamp). Mirrors the operator dispatch in <c>TonemapFrag</c>. The per-channel path calls this
        /// for each channel, the hue-preserving path calls it once on luminance.</summary>
        public static float Curve(float x, int op)
        {
            if (op == 0) return AcesFilm(x);
            if (op == 1) return x / (1f + x);
            return Math.Clamp(x, 0f, 1f);
        }

        /// <summary>
        /// Full tonemap for one texel, mirroring <c>TonemapFrag.main()</c> exactly (including the factor-0
        /// short-circuit and the fp order of operations, so the C# and GLSL results agree bit-for-bit at factor 0).
        /// <paramref name="exposure"/> is <c>Params.x</c>, <paramref name="op"/> is <c>int(Params.y + 0.5)</c>,
        /// <paramref name="chroma"/> is <c>Params.z</c> (0..1, values &lt;= 0 take the identity path).
        /// </summary>
        public static (float R, float G, float B) Map(float r, float g, float b, float exposure, int op, float chroma)
        {
            // max(s.rgb, 0) * Params.x
            float cr = MathF.Max(r, 0f) * exposure;
            float cg = MathF.Max(g, 0f) * exposure;
            float cb = MathF.Max(b, 0f) * exposure;

            // Per-channel operator: the historical look.
            float pr = Curve(cr, op);
            float pg = Curve(cg, op);
            float pb = Curve(cb, op);

            // Params.z == 0 short-circuits to the exact per-channel result (byte-identical default).
            if (chroma <= 0f) return (pr, pg, pb);

            // Hue-preserving: map luminance through the same operator, rescale RGB, hold chromaticity.
            float l = Luma(cr, cg, cb);
            float lm = Curve(l, op);
            float scale = lm / MathF.Max(l, 1e-5f);
            float hr = cr * scale;
            float hg = cg * scale;
            float hb = cb * scale;

            // mix(perChannel, huePreserving, chroma), then saturate.
            float mr = Math.Clamp(Mix(pr, hr, chroma), 0f, 1f);
            float mg = Math.Clamp(Mix(pg, hg, chroma), 0f, 1f);
            float mb = Math.Clamp(Mix(pb, hb, chroma), 0f, 1f);
            return (mr, mg, mb);
        }

        /// <summary>GLSL <c>mix(a, b, t)</c>: <c>a * (1 - t) + b * t</c>. Kept explicit so the C# mirror matches the
        /// shader's fp evaluation order.</summary>
        static float Mix(float a, float b, float t) => a * (1f - t) + b * t;
    }
}
