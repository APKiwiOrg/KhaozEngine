using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the terrain raymarcher: hits land on the surface, misses stay misses,
    /// below-origin returns immediately, and results are deterministic.</summary>
    public class TerrainRaycastTests
    {
        static TerrainField FlatField(float height = 2f) =>
            new TerrainField(new TerrainConfig
            {
                Seed = 7,
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
                },
            });

        static TerrainField RollingField() =>
            new TerrainField(new TerrainConfig
            {
                Seed = 3,
                GentleAmplitude = 2f,
                GentleFrequency = 0.05f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
                },
            });

        [Fact]
        public void DiagonalRay_HitsFlatGroundAtExpectedPoint()
        {
            var field = FlatField(2f);
            // From (0, 10, 0) descending at 45 degrees along +X: crosses y=2 at x=8.
            bool hit = TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 100f, out Vector3 p);
            Assert.True(hit);
            Assert.Equal(8f, p.X, 2);
            Assert.Equal(2f, p.Y, 2);
        }

        [Fact]
        public void HorizontalRayAboveGround_Misses()
        {
            var field = FlatField(2f);
            Assert.False(TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, 0f, 0f), 100f, out _));
        }

        [Fact]
        public void OriginBelowSurface_ReturnsOrigin()
        {
            var field = FlatField(2f);
            Assert.True(TerrainRaycast.Raycast(field, new Vector3(5f, 0f, 5f), new Vector3(0f, -1f, 0f), 10f, out Vector3 p));
            Assert.Equal(new Vector3(5f, 0f, 5f), p);
        }

        [Fact]
        public void RollingTerrain_HitLiesOnTheSurface()
        {
            var field = RollingField();
            bool hit = TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 p);
            Assert.True(hit);
            Assert.Equal(field.SampleHeight(p.X, p.Z), p.Y, 2);
        }

        [Fact]
        public void Deterministic_SameInputsSameHit()
        {
            var field = RollingField();
            TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 a);
            TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 b);
            Assert.Equal(a, b);
        }

        [Fact]
        public void MaxDistance_StopsTheMarch()
        {
            var field = FlatField(2f);
            Assert.False(TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 5f, out _));
        }

        [Fact]
        public void TailCrossing_WithinLastPartialStep_IsFound()
        {
            var field = FlatField(2.1f);
            // True crossing at t = 7.9, strictly between the last full step multiple (7.75) and maxDistance (7.95).
            bool hit = TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 7.95f, out Vector3 p);
            Assert.True(hit);
            Assert.Equal(7.9f, p.X, 2);
            Assert.Equal(2.1f, p.Y, 2);
        }

        [Fact]
        public void NonPositiveStep_Throws()
        {
            var field = FlatField(2f);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 10f, out _, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 10f, out _, -1f));
        }

        [Fact]
        public void NaNStep_Throws()
        {
            // ThrowIfNegativeOrZero passes NaN through (NaN <= 0 is false), which used to march forever with
            // t += NaN turning every subsequent sample into NaN too - a silent miss instead of a clear reject.
            var field = FlatField(2f);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 10f, out _, float.NaN));
        }

        [Fact]
        public void NaNMaxDistance_Throws()
        {
            // maxDistance had no explicit guard at all: prevT(0) < NaN is false, so the march loop never ran and the
            // call silently reported a miss instead of rejecting the bad input.
            var field = FlatField(2f);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), float.NaN, out _));
        }

        [Fact]
        public void ExtremeMaxDistanceToStepRatio_StillTerminates()
        {
            // Once t is huge relative to step, float32 addition stalls (t + step == t, at t/step past ~2^24: the
            // 24-bit mantissa can no longer represent the smaller increment) - a real reproduction inherently takes
            // that many march iterations regardless of the absolute step/maxDistance magnitudes chosen (t grows by a
            // constant step each iteration, so reaching a t/step ratio of ~2^24 always takes ~2^24 iterations). The
            // guard's whole point is to jump straight to maxDistance once that happens, terminating in ~16.7 million
            // steps instead of hanging for the nominal (and here effectively unreachable) maxDistance/step count.
            // DetailOctaves 0 keeps each of those steps to Fbm's fixed 4-octave noise call (no Turbulence term) so
            // the test completes in a few seconds instead of stacking a second octave loop on every sample.
            var field = new TerrainField(new TerrainConfig
            {
                Seed = 7,
                GentleAmplitude = 0f,
                DetailOctaves = 0,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 2f, HillAmplitude = 0f },
                },
            });
            // A horizontal ray held well above the flat ground never crosses it, so the march runs the full
            // distance without an early return, forcing it through the stall path.
            bool hit = TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, 0f, 0f), 1e12f, out _, step: 0.01f);
            Assert.False(hit);
        }

        // ---- Func<float, float, float> overload: same kernel, a bare height function instead of a TerrainField ---

        [Fact]
        public void DelegateOverload_ClosedFormFlatPlane_HitsExactExpectedPoint()
        {
            // y = 2 everywhere: a closed-form plane, not backed by any TerrainField. Same 45-degree ray as
            // DiagonalRay_HitsFlatGroundAtExpectedPoint, so the expected intersection is exact (x = 8, y = 2).
            static float FlatPlane(float x, float z) => 2f;

            bool hit = TerrainRaycast.Raycast(FlatPlane, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 100f, out Vector3 p);

            Assert.True(hit);
            Assert.Equal(8f, p.X, 2);
            Assert.Equal(2f, p.Y, 2);
            Assert.Equal(0f, p.Z, 2);
        }

        [Fact]
        public void DelegateOverload_ClosedFormSlopedPlane_HitsExactExpectedPoint()
        {
            // y = x * 0.5: straight down from (4, 10, 0) must land exactly on (4, 2, 0).
            static float SlopedPlane(float x, float z) => x * 0.5f;

            bool hit = TerrainRaycast.Raycast(SlopedPlane, new Vector3(4f, 10f, 0f), new Vector3(0f, -1f, 0f), 50f, out Vector3 p);

            Assert.True(hit);
            Assert.Equal(4f, p.X, 2);
            Assert.Equal(2f, p.Y, 2);
            Assert.Equal(0f, p.Z, 2);
        }

        [Fact]
        public void DelegateOverload_RayParallelAboveTerrain_Misses()
        {
            // Horizontal ray held above a flat plane never crosses it: same miss shape as
            // HorizontalRayAboveGround_Misses, against a bare height function instead of a TerrainField.
            static float FlatPlane(float x, float z) => 2f;

            Assert.False(TerrainRaycast.Raycast(FlatPlane, new Vector3(0f, 10f, 0f), new Vector3(1f, 0f, 0f), 100f, out _));
        }

        [Fact]
        public void DelegateOverload_AndFieldOverload_AgreeForTheSameField()
        {
            // field.SampleHeight and the delegate overload fed that same method must land on identical results:
            // the field overload is a thin adapter over the delegate kernel, so there is exactly one code path.
            var field = RollingField();
            Vector3 origin = new(-20f, 15f, 7f);
            Vector3 direction = new(1f, -0.4f, 0.1f);

            bool fieldHit = TerrainRaycast.Raycast(field, origin, direction, 200f, out Vector3 fieldPoint);
            bool delegateHit = TerrainRaycast.Raycast(field.SampleHeight, origin, direction, 200f, out Vector3 delegatePoint);

            Assert.True(fieldHit);
            Assert.Equal(fieldHit, delegateHit);
            Assert.Equal(fieldPoint, delegatePoint);
        }

        [Fact]
        public void DelegateOverload_AndFieldOverload_AgreeOnAMiss()
        {
            var field = FlatField(2f);
            Vector3 origin = new(0f, 10f, 0f);
            Vector3 direction = new(1f, 0f, 0f);

            bool fieldHit = TerrainRaycast.Raycast(field, origin, direction, 100f, out _);
            bool delegateHit = TerrainRaycast.Raycast(field.SampleHeight, origin, direction, 100f, out _);

            Assert.False(fieldHit);
            Assert.False(delegateHit);
        }

        [Fact]
        public void DelegateOverload_CustomStepAndBisectIterations_AreHonored()
        {
            // A deliberately coarse step (5m) with zero bisection lands the hit at the first crossing sample, not
            // refined onto the true surface: proves both optional parameters are actually threaded through to the
            // kernel rather than silently defaulted. True crossing is at t = 8 (y = 2 plane, 45-degree ray from
            // (0, 10, 0)); a step of 5 first crosses to below-surface at t = 10 (5, then 10), and with
            // bisectIterations: 0 that raw sample is reported verbatim, so hit.X = 10 (unrefined), not ~8.
            static float FlatPlane(float x, float z) => 2f;

            bool hit = TerrainRaycast.Raycast(FlatPlane, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 100f,
                out Vector3 p, step: 5f, bisectIterations: 0);

            Assert.True(hit);
            Assert.Equal(10f, p.X, 2);
        }

        [Fact]
        public void DelegateOverload_HigherBisectIterations_RefinesCloserToTheTrueCrossing()
        {
            // Same coarse step as the previous case, but with a real bisection budget: the refined hit must land
            // much closer to the true crossing (x = 8) than the unrefined coarse sample (x = 10) did.
            static float FlatPlane(float x, float z) => 2f;

            bool hit = TerrainRaycast.Raycast(FlatPlane, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 100f,
                out Vector3 p, step: 5f, bisectIterations: 24);

            Assert.True(hit);
            Assert.Equal(8f, p.X, 2);
        }

        [Fact]
        public void DelegateOverload_NullHeightAt_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                TerrainRaycast.Raycast((Func<float, float, float>)null!, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 10f, out _));
        }

        [Fact]
        public void NegativeBisectIterations_Throws()
        {
            var field = FlatField(2f);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 10f, out _, bisectIterations: -1));
        }
    }
}
