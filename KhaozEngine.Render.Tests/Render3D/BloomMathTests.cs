using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for <see cref="BloomMath"/>: the soft-knee threshold curve, gaussian weight
    /// generation (symmetry + normalization), and half-resolution target sizing (incl. odd sizes). No GPU;
    /// BloomMath is the single source both this test and the GLSL BloomBrightFrag/BloomBlurFrag mirror.
    /// </summary>
    public sealed class BloomMathTests
    {
        // ---- KneeWeight (soft threshold curve) --------------------------------------------------------------------

        [Fact]
        public void KneeWeight_HardThreshold_WhenKneeIsZero()
        {
            Assert.Equal(0f, BloomMath.KneeWeight(0.69f, 0.7f, 0f));
            Assert.Equal(1f, BloomMath.KneeWeight(0.70f, 0.7f, 0f));
            Assert.Equal(1f, BloomMath.KneeWeight(1.0f, 0.7f, 0f));
        }

        [Fact]
        public void KneeWeight_BelowLowEdge_IsZero()
        {
            // lo = threshold - knee = 0.7 - 0.15 = 0.55
            Assert.Equal(0f, BloomMath.KneeWeight(0.4f, 0.7f, 0.15f));
            Assert.Equal(0f, BloomMath.KneeWeight(0.55f, 0.7f, 0.15f), 5); // exactly at lo: smoothstep(0) == 0 modulo fp noise
        }

        [Fact]
        public void KneeWeight_AboveHighEdge_IsOne()
        {
            // hi = threshold + knee = 0.7 + 0.15 = 0.85
            Assert.Equal(1f, BloomMath.KneeWeight(0.85f, 0.7f, 0.15f));
            Assert.Equal(1f, BloomMath.KneeWeight(1.0f, 0.7f, 0.15f));
        }

        [Fact]
        public void KneeWeight_AtThreshold_IsHalf()
        {
            // Smoothstep at the midpoint of [lo,hi] (t=0.5) evaluates to exactly 0.5.
            float w = BloomMath.KneeWeight(0.7f, 0.7f, 0.15f);
            Assert.Equal(0.5f, w, 5);
        }

        [Fact]
        public void KneeWeight_IsMonotonicNonDecreasing_AcrossTheRamp()
        {
            float prev = -1f;
            for (float l = 0.5f; l <= 0.9f; l += 0.01f)
            {
                float w = BloomMath.KneeWeight(l, 0.7f, 0.15f);
                Assert.True(w >= prev - 1e-6f, $"KneeWeight regressed at luma {l}: {w} < {prev}");
                Assert.InRange(w, 0f, 1f);
                prev = w;
            }
        }

        [Fact]
        public void Luma_WeightsSumToOne_MatchingRec709()
        {
            // The same weights EdgeFrag/FxaaFrag use elsewhere in the post chain.
            Assert.Equal(1f, BloomMath.Luma(1f, 1f, 1f), 5);
            Assert.Equal(0f, BloomMath.Luma(0f, 0f, 0f), 5);
            Assert.Equal(0.587f, BloomMath.Luma(0f, 1f, 0f), 5);
        }

        // ---- GaussianWeights (separable blur taps) ----------------------------------------------------------------

        [Fact]
        public void GaussianWeights_Radius0_IsSingleUnitWeight()
        {
            float[] w = BloomMath.GaussianWeights(0);
            Assert.Single(w);
            Assert.Equal(1f, w[0]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void GaussianWeights_have_normalized_symmetric_centre_peaked_taps(int radius)
        {
            float[] w = BloomMath.GaussianWeights(radius);
            Assert.Equal(2 * radius + 1, w.Length);
            float sum = 0f;
            float centre = w[radius];
            for (int i = 0; i < w.Length; i++)
            {
                sum += w[i];
                Assert.Equal(w[i], w[w.Length - 1 - i], 6);
                Assert.True(centre >= w[i] - 1e-6f, "centre tap is not the maximum weight");
            }
            Assert.Equal(1f, sum, 5);
        }

        [Fact]
        public void GaussianWeights_NegativeRadius_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BloomMath.GaussianWeights(-1));
        }

        [Fact]
        public void GaussianWeights_AtRadiusFour_MatchReferenceCurve()
        {
            // Independent normalized Gaussian reference for radius 4 and sigma 2.
            float[] reference =
            {
                0.02763055f, 0.06628225f, 0.12383154f, 0.18017382f, 0.20416369f,
                0.18017382f, 0.12383154f, 0.06628225f, 0.02763055f,
            };
            float[] array = BloomMath.GaussianWeights(4);
            var buffer = new float[2 * BloomMath.MaxRadius + 1];
            BloomMath.GaussianWeights(4, buffer);
            Assert.Equal(reference.Length, array.Length);
            for (int i = 0; i < reference.Length; i++)
            {
                Assert.Equal(reference[i], array[i], 6);
                Assert.Equal(reference[i], buffer[i], 6);
            }
        }

        [Fact]
        public void GaussianWeights_IntoABuffer_WritesOnlyItsTaps()
        {
            var buffer = new float[2 * BloomMath.MaxRadius + 2];
            for (int radius = 0; radius <= BloomMath.MaxRadius; radius++)
            {
                Array.Fill(buffer, -1f);
                BloomMath.GaussianWeights(radius, buffer);
                int taps = 2 * radius + 1;
                for (int i = 0; i < taps; i++) Assert.True(buffer[i] > 0f, $"radius {radius} left tap {i} unwritten");
                for (int i = taps; i < buffer.Length; i++) Assert.Equal(-1f, buffer[i]);
            }
            Assert.Throws<ArgumentException>(() => BloomMath.GaussianWeights(2, new float[4]));
            Assert.Throws<ArgumentOutOfRangeException>(() => BloomMath.GaussianWeights(-1, buffer));
        }

        [Fact]
        public void GaussianWeights_WiderRadius_HasFlatterCentre()
        {
            // A bigger radius derives a bigger sigma, so the centre tap's share of the total energy should shrink
            // (the energy spreads across more taps) rather than staying a tight spike truncated by the cutoff.
            float centre1 = BloomMath.GaussianWeights(1)[1];
            float centre8 = BloomMath.GaussianWeights(8)[8];
            Assert.True(centre8 < centre1, "a radius-8 blur's centre weight should be smaller than radius-1's");
        }

        // ---- HalfResSize (half-resolution bloom target derivation) ------------------------------------------------

        [Theory]
        [InlineData(1600, 900, 800, 450)]
        [InlineData(480, 320, 240, 160)]
        [InlineData(2, 2, 1, 1)]
        [InlineData(481, 321, 241, 161)]
        [InlineData(1, 1, 1, 1)]
        [InlineData(3, 5, 2, 3)]
        public void HalfResSize_rounds_each_dimension_up(int w, int h, int expW, int expH)
        {
            var (hw, hh) = BloomMath.HalfResSize(w, h);
            Assert.Equal(expW, hw);
            Assert.Equal(expH, hh);
        }

        [Fact]
        public void HalfResSize_NeverUnderflowsToZero()
        {
            Assert.Equal((1, 1), BloomMath.HalfResSize(0, 0));
        }

        [Fact]
        public void HalfResSize_ReDerivedFromCurrentFullSize_TracksResize()
        {
            // MatchViewport resizes the internal target every frame the viewport changes; HalfResSize must be a
            // pure re-derivation from whatever full size is passed, not stateful.
            var (a, b) = BloomMath.HalfResSize(1920, 1080);
            var (c, d) = BloomMath.HalfResSize(1280, 720);
            Assert.Equal((960, 540), (a, b));
            Assert.Equal((640, 360), (c, d));
        }
    }
}
