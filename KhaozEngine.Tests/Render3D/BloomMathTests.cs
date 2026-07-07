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
        public void GaussianWeights_HasCorrectTapCount(int radius)
        {
            float[] w = BloomMath.GaussianWeights(radius);
            Assert.Equal(2 * radius + 1, w.Length);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void GaussianWeights_SumToOne(int radius)
        {
            float[] w = BloomMath.GaussianWeights(radius);
            float sum = 0f;
            foreach (float x in w) sum += x;
            Assert.Equal(1f, sum, 5);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void GaussianWeights_AreSymmetric(int radius)
        {
            float[] w = BloomMath.GaussianWeights(radius);
            for (int i = 0; i < w.Length; i++)
                Assert.Equal(w[i], w[w.Length - 1 - i], 6);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void GaussianWeights_PeakAtCentre(int radius)
        {
            float[] w = BloomMath.GaussianWeights(radius);
            float centre = w[radius];
            foreach (float x in w) Assert.True(centre >= x - 1e-6f, "centre tap is not the maximum weight");
        }

        [Fact]
        public void GaussianWeights_NegativeRadius_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BloomMath.GaussianWeights(-1));
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
        public void HalfResSize_EvenDimensions_ExactHalf(int w, int h, int expW, int expH)
        {
            var (hw, hh) = BloomMath.HalfResSize(w, h);
            Assert.Equal(expW, hw);
            Assert.Equal(expH, hh);
        }

        [Theory]
        [InlineData(481, 321, 241, 161)]  // odd sizes round UP, not down
        [InlineData(1, 1, 1, 1)]
        [InlineData(3, 5, 2, 3)]
        public void HalfResSize_OddDimensions_RoundUp(int w, int h, int expW, int expH)
        {
            var (hw, hh) = BloomMath.HalfResSize(w, h);
            Assert.Equal(expW, hw);
            Assert.Equal(expH, hh);
        }

        [Fact]
        public void HalfResSize_NeverUnderflowsToZero()
        {
            var (hw, hh) = BloomMath.HalfResSize(1, 1);
            Assert.True(hw >= 1 && hh >= 1);
            var (hw0, hh0) = BloomMath.HalfResSize(0, 0);
            Assert.True(hw0 >= 1 && hh0 >= 1);
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
