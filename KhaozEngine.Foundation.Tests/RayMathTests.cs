using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests
{
    /// <summary>Headless tests for the RayMath slab test: entry distance, inside-origin, misses,
    /// axis-parallel rays, and unnormalized directions.</summary>
    public class RayMathTests
    {
        static readonly Vector3 Min = new(-1f, -1f, -1f);
        static readonly Vector3 Max = new(1f, 1f, 1f);

        [Fact]
        public void HitFromOutside_ReturnsEntryDistance()
        {
            Assert.True(RayMath.IntersectAabb(new Vector3(-5f, 0f, 0f), new Vector3(1f, 0f, 0f), Min, Max, out float t));
            Assert.Equal(4f, t, 4);
        }

        [Fact]
        public void OriginInside_ReturnsZero()
        {
            Assert.True(RayMath.IntersectAabb(Vector3.Zero, new Vector3(0f, 1f, 0f), Min, Max, out float t));
            Assert.Equal(0f, t);
        }

        [Fact]
        public void Miss_ReturnsFalse()
        {
            Assert.False(RayMath.IntersectAabb(new Vector3(-5f, 3f, 0f), new Vector3(1f, 0f, 0f), Min, Max, out _));
        }

        [Fact]
        public void PointingAway_ReturnsFalse()
        {
            Assert.False(RayMath.IntersectAabb(new Vector3(-5f, 0f, 0f), new Vector3(-1f, 0f, 0f), Min, Max, out _));
        }

        [Fact]
        public void AxisParallel_InsideSlab_Hits_OutsideSlab_Misses()
        {
            Assert.True(RayMath.IntersectAabb(new Vector3(0.5f, -9f, 0.5f), new Vector3(0f, 1f, 0f), Min, Max, out float t));
            Assert.Equal(8f, t, 4);
            Assert.False(RayMath.IntersectAabb(new Vector3(2f, -9f, 0.5f), new Vector3(0f, 1f, 0f), Min, Max, out _));
        }

        [Fact]
        public void UnnormalizedDirection_ScalesT()
        {
            Assert.True(RayMath.IntersectAabb(new Vector3(-5f, 0f, 0f), new Vector3(2f, 0f, 0f), Min, Max, out float t));
            Assert.Equal(2f, t, 4);
        }

        [Fact]
        public void ZeroLengthRay_HitsOnlyWhenOriginIsInside()
        {
            // A degenerate ray (no direction) never leaves the origin. Pinned semantics: it hits, at tNear 0, only
            // when the origin already lies inside the box on every axis, and otherwise misses.
            Assert.True(RayMath.IntersectAabb(Vector3.Zero, Vector3.Zero, Min, Max, out float t));
            Assert.Equal(0f, t);
            Assert.False(RayMath.IntersectAabb(new Vector3(5f, 0f, 0f), Vector3.Zero, Min, Max, out _));
        }

        [Fact]
        public void NaNDirectionComponent_Misses()
        {
            // A NaN direction component used to fall through every comparison in SlabAxis (NaN comparisons are all
            // false) and return true unconditionally, making it an always-pass slab. It must miss instead.
            float nan = float.NaN;
            Assert.False(RayMath.IntersectAabb(Vector3.Zero, new Vector3(nan, 1f, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(Vector3.Zero, new Vector3(1f, nan, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(Vector3.Zero, new Vector3(1f, 1f, nan), Min, Max, out _));
        }

        [Fact]
        public void NaNOriginComponent_Misses()
        {
            float nan = float.NaN;
            Assert.False(RayMath.IntersectAabb(new Vector3(nan, 0f, 0f), new Vector3(1f, 1f, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(new Vector3(0f, nan, 0f), new Vector3(1f, 1f, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(new Vector3(0f, 0f, nan), new Vector3(1f, 1f, 1f), Min, Max, out _));
        }
    }
}
