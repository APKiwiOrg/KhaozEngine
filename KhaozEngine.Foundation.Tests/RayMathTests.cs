using System;
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

        static readonly Vector3 LongMin = new(-2f, -0.5f, -0.5f);
        static readonly Vector3 LongMax = new(2f, 0.5f, 0.5f);

        [Fact]
        public void ObbY_ZeroYaw_MatchesTheAabbTestAboutTheCentre()
        {
            var centre = new Vector3(10f, 0f, 0f);
            Assert.True(RayMath.IntersectObbY(
                new Vector3(5f, 0f, 0f), new Vector3(1f, 0f, 0f), centre, 0f, Min, Max, out float t));
            Assert.Equal(4f, t, 4);
        }

        [Fact]
        public void ObbY_QuarterTurn_SwapsWhichRaysHit()
        {
            // A box four long on local X and one wide on local Z, centred at the origin. Head-on down +Z, the
            // near face is at z = -0.5 unrotated and at z = -2 after a quarter turn, so the entry distance moves.
            var origin = new Vector3(0f, 0f, -5f);
            var direction = new Vector3(0f, 0f, 1f);

            Assert.True(RayMath.IntersectObbY(
                origin, direction, Vector3.Zero, 0f, LongMin, LongMax, out float flat));
            Assert.Equal(4.5f, flat, 4);

            Assert.True(RayMath.IntersectObbY(
                origin, direction, Vector3.Zero, MathF.PI / 2f, LongMin, LongMax, out float turned));
            Assert.Equal(3f, turned, 4);
        }

        [Fact]
        public void ObbY_QuarterTurn_TurnsAHitIntoAMiss()
        {
            // Same box, offset sideways so the ray passes through the long arm unrotated and clears the box
            // entirely once that arm has swung away. This is the case an AABB test cannot express.
            var origin = new Vector3(1.5f, 0f, -5f);
            var direction = new Vector3(0f, 0f, 1f);

            Assert.True(RayMath.IntersectObbY(
                origin, direction, Vector3.Zero, 0f, LongMin, LongMax, out float t));
            Assert.Equal(4.5f, t, 4);
            Assert.False(RayMath.IntersectObbY(
                origin, direction, Vector3.Zero, MathF.PI / 2f, LongMin, LongMax, out _));
        }

        [Fact]
        public void ObbY_OriginInside_ReturnsZero()
        {
            Assert.True(RayMath.IntersectObbY(
                new Vector3(3f, 0f, 3f), new Vector3(0f, 1f, 0f), new Vector3(3f, 0f, 3f),
                0.7f, Min, Max, out float t));
            Assert.Equal(0f, t);
        }

        [Fact]
        public void ObbY_UnnormalizedDirection_ScalesT()
        {
            // t stays in units of the direction's length, exactly as the AABB test documents.
            Assert.True(RayMath.IntersectObbY(
                new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 2f), Vector3.Zero,
                MathF.PI / 2f, LongMin, LongMax, out float t));
            Assert.Equal(1.5f, t, 4);
        }

        [Fact]
        public void ObbY_FullTurn_IsTheSameBoxAsNoTurn()
        {
            // A whole turn must land back on the unrotated answer within float tolerance, which pins the sign
            // convention as well as the round trip.
            var origin = new Vector3(1.5f, 0f, -5f);
            var direction = new Vector3(0f, 0f, 1f);
            Assert.True(RayMath.IntersectObbY(
                origin, direction, Vector3.Zero, MathF.Tau, LongMin, LongMax, out float t));
            Assert.Equal(4.5f, t, 3);
        }

        [Fact]
        public void ObbExitPreservesEntryAndRayUnits()
        {
            var center = new Vector3(3f, 2f, -7f);
            const float yaw = 0.7f;
            Matrix4x4 rotation = Matrix4x4.CreateRotationY(yaw);
            foreach (float speed in new[] { 1f, 2f })
            {
                foreach (float start in new[] { -5f, 0f })
                {
                    Vector3 origin = center + Vector3.Transform(new Vector3(start, 0f, 0f), rotation);
                    Vector3 direction = Vector3.Transform(Vector3.UnitX * speed, rotation);
                    Assert.True(RayMath.IntersectObbY(origin, direction, center, yaw, LongMin, LongMax, out float oldNear));
                    Assert.True(RayMath.IntersectObbY(origin, direction, center, yaw, LongMin, LongMax,
                        out float newNear, out float far));
                    Assert.Equal(oldNear, newNear);
                    Assert.Equal(start < 0f ? 3f / speed : 0f, newNear, 4);
                    Assert.Equal((2f - start) / speed, far, 4);
                    Vector3 exit = Vector3.Transform(origin + direction * far - center,
                        Matrix4x4.CreateRotationY(-yaw));
                    Assert.Equal(LongMax.X, exit.X, 4);
                    Assert.InRange(exit.Y, LongMin.Y, LongMax.Y);
                    Assert.InRange(exit.Z, LongMin.Z, LongMax.Z);
                }
            }
        }

        [Fact]
        public void ObbExitPreservesZeroDirectionAndNaNMisses()
        {
            Assert.True(RayMath.IntersectObbY(Vector3.Zero, Vector3.Zero, Vector3.Zero, 0.7f,
                Min, Max, out float near, out float far));
            Assert.Equal(0f, near);
            Assert.Equal(float.PositiveInfinity, far);
            foreach (var (origin, direction) in new[]
            {
                (new Vector3(5f, 0f, 0f), Vector3.Zero),
                (new Vector3(float.NaN, 0f, 0f), Vector3.UnitY),
                (Vector3.Zero, new Vector3(0f, float.NaN, 0f)),
                (Vector3.Zero, new Vector3(0f, 0f, float.NaN)),
            })
            {
                Assert.False(RayMath.IntersectObbY(origin, direction, Vector3.Zero, 0.7f, Min, Max, out float oldNear));
                Assert.False(RayMath.IntersectObbY(origin, direction, Vector3.Zero, 0.7f, Min, Max,
                    out float newNear, out _));
                Assert.Equal(oldNear, newNear);
            }
        }

        [Fact]
        public void NaNOriginComponent_Misses()
        {
            float nan = float.NaN;
            Assert.False(RayMath.IntersectAabb(new Vector3(nan, 0f, 0f), new Vector3(1f, 1f, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(new Vector3(0f, nan, 0f), new Vector3(1f, 1f, 1f), Min, Max, out _));
            Assert.False(RayMath.IntersectAabb(new Vector3(0f, 0f, nan), new Vector3(1f, 1f, 1f), Min, Max, out _));
        }

        [Fact]
        public void ContainsObbYHonoursYaw()
        {
            var min = new Vector3(-1f, 0f, -0.25f);
            var max = new Vector3(1f, 2f, 0.25f);
            var center = new Vector3(10f, 0f, 10f);
            float yaw = MathF.PI / 2f;
            Assert.True(RayMath.ContainsObbY(new Vector3(10f, 1f, 10.9f), center, yaw, min, max, 0f));
            Assert.False(RayMath.ContainsObbY(new Vector3(10.9f, 1f, 10f), center, yaw, min, max, 0f));
            Assert.False(RayMath.ContainsObbY(new Vector3(10.4f, 1f, 10f), center, yaw, min, max, 0f));
            Assert.True(RayMath.ContainsObbY(new Vector3(10.4f, 1f, 10f), center, yaw, min, max, 0.3f));
        }

        [Fact]
        public void ToObbLocalYMatchesIntersectObbY()
        {
            var min = new Vector3(-1f, 0f, -0.5f);
            var max = new Vector3(1.5f, 2f, 0.5f);
            var center = new Vector3(3f, 1f, -2f);
            const float yaw = 0.7f;
            var random = new Random(1234);
            for (int i = 0; i < 64; i++)
            {
                var point = center + new Vector3(
                    random.NextSingle() * 6f - 3f, random.NextSingle() * 4f - 1f, random.NextSingle() * 6f - 3f);
                bool inside = RayMath.ContainsObbY(point, center, yaw, min, max, 0f);
                bool entersAtZero = RayMath.IntersectObbY(point, Vector3.UnitX, center, yaw, min, max, out float t)
                    && t == 0f;
                Assert.Equal(entersAtZero, inside);
                Vector3 local = RayMath.ToObbLocalY(point, center, yaw);
                Assert.Equal(inside, local.X >= min.X && local.X <= max.X && local.Y >= min.Y && local.Y <= max.Y
                    && local.Z >= min.Z && local.Z <= max.Z);
            }
        }
    }
}
