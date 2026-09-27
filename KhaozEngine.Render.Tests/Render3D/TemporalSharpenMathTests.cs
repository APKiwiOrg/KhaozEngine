using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The robust contrast adaptive sharpen (RCAS, after AMD FidelityFX Super Resolution 1) as its CPU mirror. The
    /// argument for the pass being safe after the temporal resolve rests on three properties pinned here: it never
    /// leaves 0 to 1, a tap at black or white leaves no room for a lobe unless a whole ring channel sits there, and it
    /// sharpens an isolated pixel at most half as hard as an edge pixel. The exact-value cases pin the limit, the luma
    /// weights, the noise weight and the sharpness mapping, each of which the property tests alone leave free.
    /// </summary>
    public sealed class TemporalSharpenMathTests
    {
        static Vector3 G(float v) => new(v, v, v);

        [Fact]
        public void ZeroSharpnessReturnsTheCentre()
        {
            Vector3 e = new(0.3f, 0.5f, 0.7f);
            Assert.Equal(e, TemporalSharpenMath.Sharpen(G(0.1f), G(0.9f), e, G(0.4f), G(0.2f), 0f));
        }

        [Fact]
        public void AFlatNeighbourhoodIsUnchanged()
        {
            Vector3 got = TemporalSharpenMath.Sharpen(G(0.42f), G(0.42f), G(0.42f), G(0.42f), G(0.42f), 1f);
            Assert.Equal(0.42f, got.X, 6);
            Assert.Equal(0.42f, got.Z, 6);
        }

        [Fact]
        public void ATapAtBlackOrWhiteLeavesNoRoomForALobe()
        {
            // A lone black tap north, then a lone white tap south, each against a mid-grey ring and centre.
            Vector3 mid = G(0.5f);
            Assert.True(TemporalSharpenMath.Lobe(G(0f), mid, mid, mid, mid, 1f) == 0f);
            Assert.True(TemporalSharpenMath.Lobe(mid, mid, mid, mid, G(1f), 1f) == 0f);
            Assert.Equal(0.5f, TemporalSharpenMath.Sharpen(G(0f), mid, mid, mid, mid, 1f).X, 6);
            Assert.Equal(0.5f, TemporalSharpenMath.Sharpen(mid, mid, mid, mid, G(1f), 1f).X, 6);
        }

        [Fact]
        public void TheUnclampedResultNeverLeavesZeroToOne()
        {
            var rng = new Random(1149);
            Vector3 R() => new((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            float low = 0f, high = 1f;
            for (int i = 0; i < 200_000; i++)
            {
                Vector3 c = TemporalSharpenMath.SharpenUnclamped(R(), R(), R(), R(), R(), (float)rng.NextDouble());
                low = MathF.Min(low, MathF.Min(c.X, MathF.Min(c.Y, c.Z)));
                high = MathF.Max(high, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
            }
            Assert.True(low >= -1e-5f && high <= 1f + 1e-5f, $"RCAS left 0 to 1: lowest {low}, highest {high}");
        }

        [Fact]
        public void SharpeningASoftEdgeGrowsWithSharpness()
        {
            // A soft horizontal ramp: west darker, east brighter, the centre above the ring mean of 0.5.
            Vector3 b = G(0.5f), h = G(0.5f), d = G(0.4f), f = G(0.6f), e = G(0.55f);
            float quarter = TemporalSharpenMath.Sharpen(b, d, e, f, h, 0.25f).X;
            float full = TemporalSharpenMath.Sharpen(b, d, e, f, h, 1f).X;
            Assert.True(quarter > 0.55f, $"0.25 must push the centre away from the ring mean, got {quarter}");
            Assert.True(full > quarter, $"1.0 must sharpen harder than 0.25, got {full} against {quarter}");
        }

        [Fact]
        public void AnIsolatedPixelIsWeightedHalfAndAnEdgePixelWhole()
        {
            // Noise: 0.55 alone against a flat 0.5 ring. Edge: the same centre on a vertical ramp whose ring mean
            // equals it.
            Assert.Equal(0.5f, TemporalSharpenMath.NoiseWeight(G(0.5f), G(0.5f), G(0.55f), G(0.5f), G(0.5f)), 5);
            Assert.Equal(1f, TemporalSharpenMath.NoiseWeight(G(0.45f), G(0.55f), G(0.55f), G(0.55f), G(0.65f)), 5);
        }

        [Fact]
        public void TheSoftRampSharpensToItsExactValues()
        {
            // Ring lobe -1/6 and noise weight 7/8, so the lobe is -7/192 at 0.25 and -7/48 at 1. The centre becomes
            // 229/410 and 31/50. A squared or exponential sharpness mapping moves the first.
            Vector3 b = G(0.5f), h = G(0.5f), d = G(0.4f), f = G(0.6f), e = G(0.55f);
            Assert.Equal(-1.0 / 6.0, TemporalSharpenMath.RingLobe(b, d, e, f, h), 1e-6);
            Assert.Equal(0.875, TemporalSharpenMath.NoiseWeight(b, d, e, f, h), 1e-6);
            Assert.Equal(0.558537, TemporalSharpenMath.Sharpen(b, d, e, f, h, 0.25f).X, 1e-6);
            Assert.Equal(0.620000, TemporalSharpenMath.Sharpen(b, d, e, f, h, 1f).X, 1e-6);
        }

        [Fact]
        public void WhereTheRingAllowsMoreThanTheLimitTheLimitBinds()
        {
            // The ring allows -9/44, past the limit, so the lobe is the limit times the noise weight 0.9.
            Vector3 b = G(0.45f), d = G(0.5f), f = G(0.5f), h = G(0.55f), e = G(0.52f);
            Assert.Equal(-9.0 / 44.0, TemporalSharpenMath.RingLobe(b, d, e, f, h), 1e-6);
            Assert.Equal(-0.16875, TemporalSharpenMath.Lobe(b, d, e, f, h, 1f), 1e-6);
            Assert.Equal(0.561538, TemporalSharpenMath.Sharpen(b, d, e, f, h, 1f).X, 1e-6);
        }

        [Fact]
        public void AColouredNeighbourhoodPinsTheLumaWeights()
        {
            // Red and green trade across the ring and blue stands out at the centre. Half red, full green and half
            // blue give a noise weight of 0.75 and a blue of 0.7. Rec.709 weights would give 0.964 and 0.780, and
            // equal weights 0.5 and 0.65.
            Vector3 b = G(0.5f), h = G(0.5f), d = new(0.6f, 0.4f, 0.5f), f = new(0.4f, 0.6f, 0.5f);
            Vector3 e = new(0.5f, 0.5f, 0.6f);
            Assert.Equal(0.75, TemporalSharpenMath.NoiseWeight(b, d, e, f, h), 1e-6);
            Vector3 got = TemporalSharpenMath.Sharpen(b, d, e, f, h, 1f);
            Assert.Equal(0.5, got.X, 1e-6);
            Assert.Equal(0.5, got.Y, 1e-6);
            Assert.Equal(0.7, got.Z, 1e-6);
        }

        [Fact]
        public void TheNoiseWeightScalesTheLobeOnce()
        {
            // 0.55 alone against a flat 0.5 ring: the limit times the weight 0.5 gives 0.58. Without the weight the
            // centre would reach 0.7, and with it applied twice only 0.5615.
            Vector3 ring = G(0.5f), e = G(0.55f);
            Assert.Equal(-0.09375, TemporalSharpenMath.Lobe(ring, ring, e, ring, ring, 1f), 1e-6);
            Assert.Equal(0.58, TemporalSharpenMath.Sharpen(ring, ring, e, ring, ring, 1f).X, 1e-6);
        }

        [Fact]
        public void AChannelClippedAcrossTheRingSetsNoBound()
        {
            // Red clipped to 1 on all five taps, a soft grey ramp in green and blue. FSR 1 gives the ramp's -1/6,
            // not the zero a floored red bound would force on every channel.
            Vector3 b = new(1f, 0.5f, 0.5f), h = new(1f, 0.5f, 0.5f), d = new(1f, 0.4f, 0.4f), f = new(1f, 0.6f, 0.6f);
            Vector3 e = new(1f, 0.55f, 0.55f);
            Assert.Equal(-1.0 / 6.0, TemporalSharpenMath.RingLobe(b, d, e, f, h), 1e-6);
            Vector3 got = TemporalSharpenMath.Sharpen(b, d, e, f, h, 1f);
            Assert.Equal(1.0, got.X, 1e-6);
            Assert.Equal(0.62, got.Y, 1e-6);
            Assert.Equal(0.62, got.Z, 1e-6);

            // A pure red primary: green and blue are 0 on all five taps and set no bound either.
            Vector3 pb = new(0.5f, 0f, 0f), pd = new(0.4f, 0f, 0f), pf = new(0.6f, 0f, 0f), pe = new(0.55f, 0f, 0f);
            Assert.Equal(-1.0 / 6.0, TemporalSharpenMath.RingLobe(pb, pd, pe, pf, pb), 1e-6);
            Vector3 primary = TemporalSharpenMath.Sharpen(pb, pd, pe, pf, pb, 1f);
            Assert.Equal(0.62, primary.X, 1e-6);
            Assert.Equal(0f, primary.Y);
            Assert.Equal(0f, primary.Z);
        }

        [Fact]
        public void ABlackOrWhiteFlatNeighbourhoodIsUnchanged()
        {
            // Every channel sits at one extreme on every tap, so no channel sets a bound and the lobe is live, yet the
            // weighted blend returns the centre exactly.
            foreach (float v in new[] { 0f, 1f })
            {
                Assert.Equal(-TemporalSharpenMath.NoBound, TemporalSharpenMath.RingLobe(G(v), G(v), G(v), G(v), G(v)));
                Assert.Equal(G(v), TemporalSharpenMath.Sharpen(G(v), G(v), G(v), G(v), G(v), 1f));
            }
        }

        [Fact]
        public void OnAGridWithBlackAndWhiteTheUnclampedResultNeverLeavesZeroToOne()
        {
            // Taps drawn from 0, 0.5 and 1, so whole ring channels at an extreme come up often.
            var rng = new Random(1150);
            Vector3 R() => new(rng.Next(3) * 0.5f, rng.Next(3) * 0.5f, rng.Next(3) * 0.5f);
            float low = 0f, high = 1f;
            int liveAtAnExtreme = 0;
            for (int i = 0; i < 200_000; i++)
            {
                Vector3 b = R(), d = R(), e = R(), f = R(), h = R();
                Vector3 mn4 = Vector3.Min(Vector3.Min(b, d), Vector3.Min(f, h));
                Vector3 mx4 = Vector3.Max(Vector3.Max(b, d), Vector3.Max(f, h));
                bool extreme = mx4.X == 0f || mx4.Y == 0f || mx4.Z == 0f || mn4.X == 1f || mn4.Y == 1f || mn4.Z == 1f;
                if (extreme && TemporalSharpenMath.RingLobe(b, d, e, f, h) < 0f) liveAtAnExtreme++;
                Vector3 c = TemporalSharpenMath.SharpenUnclamped(b, d, e, f, h, (float)rng.NextDouble());
                low = MathF.Min(low, MathF.Min(c.X, MathF.Min(c.Y, c.Z)));
                high = MathF.Max(high, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
            }
            Assert.True(liveAtAnExtreme > 0, "no neighbourhood with a whole ring channel at an extreme kept a lobe");
            Assert.True(low >= -1e-5f && high <= 1f + 1e-5f, $"RCAS left 0 to 1: lowest {low}, highest {high}");
        }

        [Fact]
        public void EachChannelTakesItsOwnLumaWeight()
        {
            // The coloured neighbourhood above trades red against green, so a swap of the two weights would pass it.
            // One unit channel at a time pins each weight to its channel.
            Assert.Equal(0.5f, TemporalSharpenMath.Luma2(Vector3.UnitX));
            Assert.Equal(1f, TemporalSharpenMath.Luma2(Vector3.UnitY));
            Assert.Equal(0.5f, TemporalSharpenMath.Luma2(Vector3.UnitZ));
        }

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(float.NegativeInfinity, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(0f, 0f)]
        [InlineData(0.25f, 0.25f)]
        [InlineData(1f, 1f)]
        [InlineData(2f, 1f)]
        [InlineData(float.PositiveInfinity, 1f)]
        public void TheSharpnessMapsToZeroToOneWithNaNAsZero(float sharpness, float applied)
        {
            Assert.Equal(applied, TemporalSharpenMath.ResolvedSharpness(sharpness));
        }

        [Fact]
        public void ANaNSharpnessIsOff()
        {
            Vector3 b = G(0.5f), h = G(0.5f), d = G(0.4f), f = G(0.6f), e = G(0.55f);
            Assert.Equal(e, TemporalSharpenMath.Sharpen(b, d, e, f, h, float.NaN));
            Assert.True(TemporalSharpenMath.Lobe(b, d, e, f, h, float.NaN) == 0f);
            Assert.Equal(e, TemporalSharpenMath.SharpenUnclamped(b, d, e, f, h, float.NaN));
        }
    }
}
