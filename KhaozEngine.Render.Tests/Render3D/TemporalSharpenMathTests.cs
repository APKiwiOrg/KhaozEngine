using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The robust contrast adaptive sharpen (RCAS, after AMD FidelityFX Super Resolution 1) as its CPU mirror. The
    /// argument for the pass being safe after the temporal resolve rests on three properties pinned here: it never
    /// leaves 0 to 1, it does nothing to a neighbourhood that already spans full contrast, and it sharpens an
    /// isolated pixel at most half as hard as an edge pixel.
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
        public void ARingThatAlreadySpansBlackToWhiteIsLeftAlone()
        {
            // North black, south white: the resolve already anti-aliased this edge and no lobe fits inside 0 to 1.
            Vector3 got = TemporalSharpenMath.Sharpen(G(0f), G(0.5f), G(0.5f), G(0.5f), G(1f), 1f);
            Assert.Equal(0.5f, got.X, 6);
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
    }
}
