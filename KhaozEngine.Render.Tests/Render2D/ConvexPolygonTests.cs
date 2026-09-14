using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless geometry tests for <see cref="ConvexPolygon"/>: the signed-area winding convention, the mitred
    /// offset in both windings and both directions, the miter clamp, the inset limit, and the draw guards the
    /// polygon primitives on <see cref="PrimitiveRenderer"/> share. No GPU.
    /// </summary>
    public class ConvexPolygonTests
    {
        const float Eps = 1e-4f;

        // Clockwise as drawn on a y-down screen: top-left, top-right, bottom-right, bottom-left.
        static Vector2[] UnitSquare() => new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };

        static Vector2[] Reversed(Vector2[] points)
        {
            var copy = (Vector2[])points.Clone();
            Array.Reverse(copy);
            return copy;
        }

        static Vector2[] RegularPolygon(int sides, float radius, Vector2 center)
        {
            var points = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float angle = MathF.Tau * i / sides;
                points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }
            return points;
        }

        // --- SignedArea -------------------------------------------------------------------------------

        [Fact]
        public void SignedArea_IsPositiveForScreenClockwiseAndNegativeForTheOtherWinding()
        {
            Assert.Equal(1f, ConvexPolygon.SignedArea(UnitSquare()), Eps);
            Assert.Equal(-1f, ConvexPolygon.SignedArea(Reversed(UnitSquare())), Eps);
        }

        [Fact]
        public void SignedArea_IsTranslationInvariantAtScreenScaleCoordinates()
        {
            Vector2 offset = new(1900f, 1060f);
            Vector2[] square = UnitSquare();
            for (int i = 0; i < square.Length; i++) square[i] = square[i] * 8f + offset;
            Assert.Equal(64f, ConvexPolygon.SignedArea(square), Eps);
        }

        [Fact]
        public void SignedArea_IsZeroForFewerThanThreePointsOrCollinearPoints()
        {
            Assert.Equal(0f, ConvexPolygon.SignedArea(ReadOnlySpan<Vector2>.Empty));
            Assert.Equal(0f, ConvexPolygon.SignedArea(new[] { new Vector2(0f, 0f), new Vector2(4f, 2f) }));
            Assert.Equal(0f, ConvexPolygon.SignedArea(new[] { new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(2f, 2f) }));
        }

        // --- Offset -----------------------------------------------------------------------------------

        [Theory]
        [InlineData(false, 0.25f)]
        [InlineData(false, -0.25f)]
        [InlineData(true, 0.25f)]
        [InlineData(true, -0.25f)]
        public void Offset_MovesEveryUnitSquareCornerDiagonallyByTheDistance(bool reversed, float distance)
        {
            Vector2[] square = reversed ? Reversed(UnitSquare()) : UnitSquare();
            var ring = new Vector2[4];

            ConvexPolygon.Offset(square, distance, ring);

            // Positive grows the square on every side and negative shrinks it, whichever way the corners run.
            for (int i = 0; i < 4; i++)
            {
                float expectedX = square[i].X < 0.5f ? -distance : 1f + distance;
                float expectedY = square[i].Y < 0.5f ? -distance : 1f + distance;
                Assert.Equal(expectedX, ring[i].X, Eps);
                Assert.Equal(expectedY, ring[i].Y, Eps);
            }
        }

        [Theory]
        [InlineData(false, 3f)]
        [InlineData(false, -3f)]
        [InlineData(true, 3f)]
        [InlineData(true, -3f)]
        public void Offset_ShiftsEveryEdgeLineByTheDistanceOnTheOutwardSide(bool reversed, float distance)
        {
            // An irregular convex pentagon with no corner sharp enough to hit the default miter clamp.
            Vector2[] polygon =
            {
                new(0f, 0f), new(40f, -6f), new(62f, 20f), new(38f, 48f), new(-4f, 30f),
            };
            if (reversed) polygon = Reversed(polygon);
            var ring = new Vector2[polygon.Length];

            ConvexPolygon.Offset(polygon, distance, ring);

            float side = MathF.Sign(ConvexPolygon.SignedArea(polygon));
            for (int i = 0; i < polygon.Length; i++)
            {
                int next = (i + 1) % polygon.Length;
                Vector2 edge = Vector2.Normalize(polygon[next] - polygon[i]);
                Vector2 outward = new Vector2(edge.Y, -edge.X) * side;
                // Both ends of the offset edge sit `distance` along the original edge's outward normal.
                Assert.Equal(distance, Vector2.Dot(ring[i] - polygon[i], outward), 1e-3f);
                Assert.Equal(distance, Vector2.Dot(ring[next] - polygon[i], outward), 1e-3f);
            }
        }

        [Theory]
        [InlineData(2f)]
        [InlineData(-2f)]
        public void Offset_ClampsTheMiterOfASharpCornerToTheLimit(float distance)
        {
            // A tall thin triangle: the apex half-angle is about 1.15 degrees, so its unclamped miter is about 50x.
            Vector2 apex = new(0f, -50f);
            Vector2[] triangle = { apex, new Vector2(1f, 0f), new Vector2(-1f, 0f) };
            var clamped = new Vector2[3];
            var loose = new Vector2[3];

            ConvexPolygon.Offset(triangle, distance, clamped, miterLimit: 4f);
            ConvexPolygon.Offset(triangle, distance, loose, miterLimit: 1000f);

            Vector2 moved = clamped[0] - apex;
            Assert.Equal(4f * MathF.Abs(distance), moved.Length(), 1e-3f);
            Assert.Equal(0f, moved.X, 1e-3f);                                  // still on the bisector
            Assert.Equal(-MathF.Sign(distance), MathF.Sign(moved.Y));          // outward is up, past the apex

            float halfAngle = MathF.Atan2(1f, 50f);
            Assert.Equal(MathF.Abs(distance) / MathF.Sin(halfAngle), (loose[0] - apex).Length(), 0.05f);

            // The blunt base corners stay far under the limit, so the clamp leaves them as the true miter.
            Assert.Equal(loose[1].X, clamped[1].X, Eps);
            Assert.Equal(loose[1].Y, clamped[1].Y, Eps);
        }

        [Fact]
        public void Offset_RingsFromOnePolygonStayOnTheSameBisectorRays()
        {
            Vector2[] triangle = { new(0f, -50f), new(1f, 0f), new(-1f, 0f) };
            var near = new Vector2[3];
            var far = new Vector2[3];

            ConvexPolygon.Offset(triangle, 1f, near);
            ConvexPolygon.Offset(triangle, 3f, far);

            // Displacement is linear in the distance, the clamped apex included, so quads between rings meet exactly.
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(3f * (near[i] - triangle[i]).X, (far[i] - triangle[i]).X, 1e-3f);
                Assert.Equal(3f * (near[i] - triangle[i]).Y, (far[i] - triangle[i]).Y, 1e-3f);
            }
        }

        [Fact]
        public void Offset_ARepeatedVertexMovesWithItsTwin()
        {
            Vector2[] square = { new(0f, 0f), new(1f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f) };
            var ring = new Vector2[5];

            ConvexPolygon.Offset(square, 0.5f, ring);

            Assert.Equal(new Vector2(1.5f, -0.5f), ring[1]);
            Assert.Equal(ring[1], ring[2]);
            Assert.All(ring, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y)));
        }

        [Fact]
        public void Offset_CopiesDegenerateInputThroughUnchanged()
        {
            Vector2[] line = { new(0f, 0f), new(1f, 1f), new(2f, 2f) };
            var ring = new Vector2[3];
            ConvexPolygon.Offset(line, 5f, ring);
            Assert.Equal(line, ring);

            Vector2[] pair = { new(3f, 4f), new(5f, 6f) };
            var pairRing = new Vector2[2];
            ConvexPolygon.Offset(pair, 5f, pairRing);
            Assert.Equal(pair, pairRing);
        }

        [Fact]
        public void Offset_RejectsAShortOrOverlappingDestination()
        {
            Vector2[] square = UnitSquare();
            Assert.Throws<ArgumentException>(() => ConvexPolygon.Offset(square, 1f, new Vector2[3]));
            Assert.Throws<ArgumentException>(() => ConvexPolygon.Offset(square, 1f, square));
        }

        // --- InsetLimit -------------------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InsetLimit_IsHalfTheSmallerSideOfARectangle(bool reversed)
        {
            Vector2[] square = { new(10f, 10f), new(14f, 10f), new(14f, 14f), new(10f, 14f) };
            Vector2[] wide = { new(0f, 0f), new(8f, 0f), new(8f, 2f), new(0f, 2f) };
            if (reversed) { square = Reversed(square); wide = Reversed(wide); }

            Assert.Equal(2f, ConvexPolygon.InsetLimit(square), Eps);
            Assert.Equal(1f, ConvexPolygon.InsetLimit(wide), Eps);
        }

        [Fact]
        public void InsetLimit_IsTheInradiusOfARegularPolygon()
        {
            Vector2[] circle = RegularPolygon(24, 20f, new Vector2(300f, 200f));
            Assert.Equal(20f * MathF.Cos(MathF.PI / 24f), ConvexPolygon.InsetLimit(circle), 1e-3f);
        }

        [Fact]
        public void InsetLimit_IsZeroForDegenerateInput()
        {
            Assert.Equal(0f, ConvexPolygon.InsetLimit(new[] { new Vector2(0f, 0f), new Vector2(4f, 0f) }));
            Assert.Equal(0f, ConvexPolygon.InsetLimit(new[] { new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(2f, 2f) }));
            Assert.Equal(0f, ConvexPolygon.InsetLimit(new[] { new Vector2(3f, 3f), new Vector2(3f, 3f), new Vector2(3f, 3f) }));
        }

        [Fact]
        public void InsetLimit_MeasuresToTheEdgeLines()
        {
            // Offsetting a square inward by its own limit collapses every corner onto the centre.
            Vector2[] square = { new(0f, 0f), new(6f, 0f), new(6f, 6f), new(0f, 6f) };
            var ring = new Vector2[4];
            ConvexPolygon.Offset(square, -ConvexPolygon.InsetLimit(square), ring);
            Assert.All(ring, p =>
            {
                Assert.Equal(3f, p.X, Eps);
                Assert.Equal(3f, p.Y, Eps);
            });
        }

        // --- Stroke ring placement and the shared draw guard -----------------------------------------

        [Theory]
        [InlineData(StrokeAlignment.Inside, 0f, -4f)]
        [InlineData(StrokeAlignment.Center, 2f, -2f)]
        [InlineData(StrokeAlignment.Outside, 4f, 0f)]
        public void StrokeRingDistances_PlaceTheRingsByAlignment(StrokeAlignment alignment, float outer, float inner)
        {
            (float o, float i) = ConvexPolygon.StrokeRingDistances(4f, alignment);
            Assert.Equal(outer, o, Eps);
            Assert.Equal(inner, i, Eps);
        }

        [Fact]
        public void IsDrawable_RefusesShortDegenerateAndNonFinitePolygons()
        {
            Assert.True(ConvexPolygon.IsDrawable(UnitSquare()));
            Assert.True(ConvexPolygon.IsDrawable(Reversed(UnitSquare())));
            Assert.False(ConvexPolygon.IsDrawable(new[] { new Vector2(0f, 0f), new Vector2(1f, 0f) }));
            Assert.False(ConvexPolygon.IsDrawable(new[] { new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(2f, 2f) }));
            Assert.False(ConvexPolygon.IsDrawable(new[] { new Vector2(0f, 0f), new Vector2(float.NaN, 0f), new Vector2(0f, 1f) }));
            Assert.False(ConvexPolygon.IsDrawable(new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, float.PositiveInfinity) }));
        }
    }
}
