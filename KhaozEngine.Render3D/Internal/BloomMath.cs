using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure bloom math shared between the C# host (settings plumbing + headless tests) and the GLSL
    /// <c>BloomBrightFrag</c>/<c>BloomBlurFrag</c>, which mirror <see cref="KneeWeight"/> and
    /// <see cref="GaussianWeights"/> exactly (keep in sync, like <c>OutlineMath</c> mirrors <c>EdgeFrag</c>). No GPU
    /// state, no allocations beyond the returned weight array.
    /// </summary>
    internal static class BloomMath
    {
        /// <summary>
        /// Soft-knee bright-pass curve: how much of a pixel's colour survives the threshold, in [0,1]. Below
        /// <paramref name="threshold"/> minus the knee width, nothing passes (0); above it, everything passes (1);
        /// in between a smoothstep ramps between the two so the cutoff doesn't hard-edge (banding/aliasing on the
        /// bloom's silhouette). <paramref name="knee"/> is the ramp half-width in luma units (0 = a hard threshold,
        /// matching the brief's "a hard threshold with a small smoothstep knee is acceptable"). Mirrors the GLSL
        /// <c>kneeWeight</c> function in <c>BloomBrightFrag</c> exactly.
        /// </summary>
        public static float KneeWeight(float luma, float threshold, float knee)
        {
            if (knee <= 0f) return luma >= threshold ? 1f : 0f;
            float lo = threshold - knee;
            float hi = threshold + knee;
            if (luma <= lo) return 0f;
            if (luma >= hi) return 1f;
            float t = (luma - lo) / (hi - lo);
            return t * t * (3f - 2f * t); // smoothstep
        }

        /// <summary>Rec. 709 relative luma of a linear-ish LDR colour (the same weights <c>EdgeFrag</c>/<c>FxaaFrag</c>
        /// use elsewhere in the post chain, kept consistent across passes).</summary>
        public static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

        /// <summary>
        /// Normalized 1D Gaussian sample weights for a separable blur of <paramref name="radius"/> taps per side
        /// (so <c>2*radius+1</c> total taps, radius &gt;= 0). Sigma is derived from the radius
        /// (<c>radius / 2</c>, clamped away from 0) so a bigger radius gives a visibly wider spread rather than a
        /// tight bell truncated by a hard cutoff. The returned array sums to 1 (energy-preserving: a flat input
        /// stays flat after the blur) and is symmetric (<c>weights[i] == weights[N-1-i]</c>). Mirrors the GLSL
        /// constant-array unroll in <c>BloomBlurFrag</c> (built at C# call time from the same formula the shader
        /// bakes in for the max supported radius; see <see cref="MaxRadius"/>).
        /// </summary>
        public static float[] GaussianWeights(int radius)
        {
            if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "radius must be >= 0.");
            int taps = 2 * radius + 1;
            var w = new float[taps];
            if (radius == 0) { w[0] = 1f; return w; }

            float sigma = MathF.Max(radius / 2f, 1e-4f);
            float twoSigma2 = 2f * sigma * sigma;
            double sum = 0;
            for (int i = 0; i < taps; i++)
            {
                float x = i - radius;
                float v = MathF.Exp(-(x * x) / twoSigma2);
                w[i] = v;
                sum += v;
            }
            float invSum = (float)(1.0 / sum);
            for (int i = 0; i < taps; i++) w[i] *= invSum;
            return w;
        }

        /// <summary>The largest blur radius (taps per side) the shader's fixed-size unroll supports (see
        /// <c>BloomBlurFrag</c>'s <c>MaxRadius</c>-sized constant array). <see cref="BloomSettings.Radius"/> is
        /// clamped to <c>[0, MaxRadius]</c> when the pass runs.</summary>
        public const int MaxRadius = 8;

        /// <summary>
        /// Half-resolution bloom target size for a <paramref name="fullWidth"/> x <paramref name="fullHeight"/>
        /// internal render target: each axis divided by 2 and rounded UP (odd sizes get one extra texel rather than
        /// losing a row/column of the source), then clamped to at least 1x1 (a 1x1 or degenerate target never
        /// underflows to 0, which would be an invalid GPU texture). Re-derived every resize from the CURRENT
        /// internal target size, so both <see cref="RenderScale.FixedInternal"/> (a constant size) and
        /// <see cref="RenderScale.MatchViewport"/> (resizes with the viewport) stay correct automatically.
        /// </summary>
        public static (int W, int H) HalfResSize(int fullWidth, int fullHeight)
        {
            int w = Math.Max(1, (fullWidth + 1) / 2);
            int h = Math.Max(1, (fullHeight + 1) / 2);
            return (w, h);
        }
    }
}
