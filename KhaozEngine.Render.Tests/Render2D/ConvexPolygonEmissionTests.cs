using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// What <see cref="PrimitiveRenderer.FillConvexPolygon"/> and <see cref="PrimitiveRenderer.StrokeConvexPolygon"/>
    /// hand the batch, counted device-free through <see cref="FakeGpuDevice"/> and <see cref="SpriteBatch.FrameStats"/>.
    /// The quad count is the observable that separates a stroke (one quad per edge, so translucent joins never
    /// double-blend) from a collapsed stroke (a triangle fan of the outer ring) and from a no-op.
    /// </summary>
    public sealed class ConvexPolygonEmissionTests
    {
        const int W = 64, H = 64;
        static readonly Color Marker = new(1f, 0.8f, 0.2f, 0.5f);

        static Vector2[] Square(float side) => new[]
        {
            new Vector2(10f, 10f), new Vector2(10f + side, 10f), new Vector2(10f + side, 10f + side), new Vector2(10f, 10f + side),
        };

        static Vector2[] Circle(int sides, float radius)
        {
            var points = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float angle = MathF.Tau * i / sides;
                points[i] = new Vector2(32f, 32f) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }
            return points;
        }

        static Vector2[] Reversed(Vector2[] points)
        {
            var copy = (Vector2[])points.Clone();
            Array.Reverse(copy);
            return copy;
        }

        // One frame through a device-free batch, returning the quads the draw emitted.
        static int QuadsEmitted(Action<PrimitiveRenderer, SpriteBatch> draw)
        {
            var device = new FakeGpuDevice();
            IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, target);
            using var batch = new SpriteBatch(device, framebuffer.Outputs);
            var white = new Texture2D(device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled)), 1, 1);
            var primitives = new PrimitiveRenderer(white);

            batch.NewFrame(device.Factory.CreateCommandList(), W, H);
            batch.Begin();
            draw(primitives, batch);
            batch.End();
            return batch.FrameStats.Quads;
        }

        // --- Fill -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fill_IsATriangleFanOfVertexCountMinusTwo(bool reversed)
        {
            Vector2[] square = reversed ? Reversed(Square(20f)) : Square(20f);
            Vector2[] circle = reversed ? Reversed(Circle(24, 20f)) : Circle(24, 20f);

            Assert.Equal(2, QuadsEmitted((p, b) => p.FillConvexPolygon(b, square, Marker)));
            Assert.Equal(22, QuadsEmitted((p, b) => p.FillConvexPolygon(b, circle, Marker)));
        }

        [Fact]
        public void FeatheredFill_AddsOneFadeQuadPerEdge()
        {
            Assert.Equal(2 + 4, QuadsEmitted((p, b) => p.FillConvexPolygon(b, Square(20f), Marker, feather: 1f)));
            Assert.Equal(22 + 24, QuadsEmitted((p, b) => p.FillConvexPolygon(b, Circle(24, 20f), Marker, feather: 1f)));
        }

        [Fact]
        public void Fill_AboveTheStackVertexCountStillDrawsTheWholeFan()
        {
            Vector2[] circle = Circle(96, 30f);
            Assert.Equal(94 + 96, QuadsEmitted((p, b) => p.FillConvexPolygon(b, circle, Marker, feather: 1f)));
        }

        // --- Stroke -----------------------------------------------------------------------------------

        [Theory]
        [InlineData(StrokeAlignment.Inside, false)]
        [InlineData(StrokeAlignment.Center, false)]
        [InlineData(StrokeAlignment.Outside, false)]
        [InlineData(StrokeAlignment.Inside, true)]
        [InlineData(StrokeAlignment.Center, true)]
        [InlineData(StrokeAlignment.Outside, true)]
        public void Stroke_EmitsExactlyOneQuadPerEdge(StrokeAlignment alignment, bool reversed)
        {
            Vector2[] square = reversed ? Reversed(Square(20f)) : Square(20f);
            Vector2[] circle = reversed ? Reversed(Circle(24, 20f)) : Circle(24, 20f);

            Assert.Equal(4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 2f, Marker, alignment)));
            Assert.Equal(24, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, circle, 2f, Marker, alignment)));
        }

        [Fact]
        public void FeatheredStroke_FadesBothEdges()
        {
            Assert.Equal(4 + 4 + 4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, Square(20f), 2f, Marker, feather: 1f)));
        }

        [Fact]
        public void Stroke_AboveTheStackVertexCountStillEmitsOneQuadPerEdge()
        {
            Vector2[] circle = Circle(96, 30f);
            Assert.Equal(96 * 3, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, circle, 2f, Marker, feather: 1f)));
        }

        [Fact]
        public void InsideStrokeAtOrPastTheInsetLimit_CollapsesToAFillOfTheOuterRing()
        {
            // A 6 wide square insets 3 before it inverts.
            Vector2[] square = Square(6f);

            Assert.Equal(4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 2.9f, Marker, StrokeAlignment.Inside)));
            Assert.Equal(2, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 3f, Marker, StrokeAlignment.Inside)));
            Assert.Equal(2, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 8f, Marker, StrokeAlignment.Inside)));
            // Collapsed, only the outer edge is exposed, so only it takes a fade strip.
            Assert.Equal(2 + 4, QuadsEmitted((p, b) =>
                p.StrokeConvexPolygon(b, square, 8f, Marker, StrokeAlignment.Inside, feather: 1f)));
        }

        [Fact]
        public void CenterStrokeCollapsesOnItsHalfThickness_AndOutsideNeverCollapses()
        {
            Vector2[] square = Square(6f);

            Assert.Equal(4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 5.8f, Marker, StrokeAlignment.Center)));
            Assert.Equal(2, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 6f, Marker, StrokeAlignment.Center)));
            Assert.Equal(4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, square, 40f, Marker, StrokeAlignment.Outside)));
        }

        // --- Guards -----------------------------------------------------------------------------------

        static readonly Vector2[][] UndrawablePolygons =
        {
            Array.Empty<Vector2>(),
            new[] { new Vector2(0f, 0f), new Vector2(5f, 0f) },
            new[] { new Vector2(0f, 0f), new Vector2(5f, 5f), new Vector2(10f, 10f) },
            new[] { new Vector2(0f, 0f), new Vector2(float.NaN, 0f), new Vector2(0f, 5f) },
            new[] { new Vector2(0f, 0f), new Vector2(5f, 0f), new Vector2(0f, float.NegativeInfinity) },
        };

        [Theory]
        [InlineData(0)]   // empty
        [InlineData(1)]   // two points
        [InlineData(2)]   // collinear, zero area
        [InlineData(3)]   // a NaN vertex
        [InlineData(4)]   // an infinite vertex
        public void UndrawablePolygons_EmitNothing(int index)
        {
            Vector2[] points = UndrawablePolygons[index];
            Assert.Equal(0, QuadsEmitted((p, b) => p.FillConvexPolygon(b, points, Marker, feather: 1f)));
            Assert.Equal(0, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, points, 2f, Marker, feather: 1f)));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-2f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void AStrokeWithoutAPositiveFiniteThickness_EmitsNothing(float thickness)
        {
            Assert.Equal(0, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, Square(20f), thickness, Marker)));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void AFeatherThatIsNotAPositiveFiniteNumber_AddsNoFade(float feather)
        {
            Assert.Equal(2, QuadsEmitted((p, b) => p.FillConvexPolygon(b, Square(20f), Marker, feather)));
            Assert.Equal(4, QuadsEmitted((p, b) => p.StrokeConvexPolygon(b, Square(20f), 2f, Marker, feather: feather)));
        }
    }
}
