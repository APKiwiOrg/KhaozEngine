using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless geometry tests for <see cref="PrimitiveRenderer"/>: the pure progress-bar layout, the
    /// radius-adaptive ring segment count, and the rotated-corner build behind the rotated SpriteBatch
    /// overload. No GPU. The visual output is covered by the gated <c>scene2d_primitives</c> golden.
    /// </summary>
    public class PrimitiveRendererTests
    {
        const float Eps = 1e-4f;

        // --- ComputeProgressBarLayout (float port of the 4.x integer geometry) ----------------------

        [Fact]
        public void NormalBar_KeepsRequestedBorderAndInsetFill()
        {
            (Rect fill, float border) = PrimitiveRenderer.ComputeProgressBarLayout(
                new Rect(10, 20, 96, 4), 0.5f, 1f);

            Assert.Equal(1f, border, Eps);
            Assert.Equal(11f, fill.X, Eps);            // 10 + border
            Assert.Equal(21f, fill.Y, Eps);            // 20 + border
            Assert.Equal(47f, fill.Width, Eps);        // (96 - 2) * 0.5
            Assert.Equal(2f, fill.Height, Eps);        // 4 - 2*border
        }

        [Fact]
        public void TwoPixelTallBar_CapsBorderSoFillStaysVisible()
        {
            // 2px tall, 1px requested border. The cap keeps >= 1px of inner space on the smaller axis:
            // maxBorder = (min(57,2) - 1)/2 = 0.5, so the border drops to 0.5 and a 1px fill strip survives.
            (Rect fill, float border) = PrimitiveRenderer.ComputeProgressBarLayout(
                new Rect(0, 0, 57, 2), 0.8f, 1f);

            Assert.Equal(0.5f, border, Eps);
            Assert.Equal(1f, fill.Height, Eps);        // 2 - 2*0.5, full minus the (capped) border
            Assert.True(fill.Width > 0f);              // (57 - 1) * 0.8 = 44.8
        }

        [Fact]
        public void ProgressIsClampedToZeroAndOne()
        {
            (Rect empty, _) = PrimitiveRenderer.ComputeProgressBarLayout(
                new Rect(0, 0, 100, 10), -0.5f, 1f);
            Assert.Equal(0f, empty.Width, Eps);

            (Rect full, _) = PrimitiveRenderer.ComputeProgressBarLayout(
                new Rect(0, 0, 100, 10), 2f, 1f);
            Assert.Equal(98f, full.Width, Eps);        // (100 - 2) * 1.0
        }

        [Fact]
        public void ZeroBorder_FillSpansFullBounds()
        {
            (Rect fill, float border) = PrimitiveRenderer.ComputeProgressBarLayout(
                new Rect(5, 6, 80, 12), 1f, 0f);

            Assert.Equal(0f, border, Eps);
            Assert.Equal(5f, fill.X, Eps);
            Assert.Equal(80f, fill.Width, Eps);
            Assert.Equal(12f, fill.Height, Eps);
        }

        // --- RingSegments (radius-adaptive clamp [18, 64], override floored at 3) --------------------

        [Theory]
        [InlineData(0f, 18)]      // degenerate radius still clamps up to the floor
        [InlineData(10f, 18)]     // (int)(3.5) = 3, clamped to 18
        [InlineData(100f, 35)]    // (int)(35.0) = 35, inside the band
        [InlineData(300f, 64)]    // (int)(105) = 105, clamped to 64
        [InlineData(5000f, 64)]   // far past the ceiling
        public void RingSegments_AdaptiveClampToBand(float radius, int expected)
        {
            Assert.Equal(expected, PrimitiveRenderer.RingSegments(radius, segmentsOverride: null));
        }

        [Theory]
        [InlineData(48, 48)]   // explicit override is honored as-is
        [InlineData(3, 3)]     // exactly the floor
        [InlineData(2, 3)]     // below the floor is raised to 3
        [InlineData(0, 3)]
        public void RingSegments_OverrideFlooredAtThree(int requested, int expected)
        {
            Assert.Equal(expected, PrimitiveRenderer.RingSegments(radius: 999f, segmentsOverride: requested));
        }

        // --- FilledCircleRowStep (band-count cap for DrawFilledCircle, mirrors the RingSegments/
        // SectorSegments clamps) ------------------------------------------------------------------------

        [Theory]
        [InlineData(0f, 1)]      // degenerate radius: 1 row, well under the cap
        [InlineData(42f, 1)]     // the scene2d_primitives golden's radius: must stay step 1 (byte-identical)
        [InlineData(63f, 1)]     // 2*63+1 = 127 <= 128, still uncapped
        [InlineData(64f, 2)]     // 2*64+1 = 129 > 128 -> smallest step that brings the band count under the cap
        [InlineData(1000f, 16)]  // 2*1000+1 = 2001 rows uncapped -> ceil(2001/128) = 16
        public void FilledCircleRowStep_IsOneUnderTheCap_ThenGrowsToStayUnderIt(float radius, int expectedStep)
        {
            Assert.Equal(expectedStep, PrimitiveRenderer.FilledCircleRowStep(radius));
        }

        [Theory]
        [InlineData(64f)]
        [InlineData(300f)]
        [InlineData(1000f)]
        [InlineData(50000f)]
        public void FilledCircleRowStep_KeepsTheBandCountAtOrUnderTheCap(float radius)
        {
            int intRadius = (int)radius;
            int step = PrimitiveRenderer.FilledCircleRowStep(radius);
            int bandCount = 0;
            for (int y = -intRadius; y <= intRadius; y += step) bandCount++;

            Assert.True(bandCount <= PrimitiveRenderer.MaxFilledCircleRows,
                $"radius {radius} produced {bandCount} bands, cap is {PrimitiveRenderer.MaxFilledCircleRows}");
        }

        // --- Rotated-corner build (SpriteBatch.RotatedCorner) ----------------------------------------

        [Fact]
        public void RotatedCorner_AtZeroRotationOriginZero_MatchesAxisAlignedRect()
        {
            // rotation 0, origin (0,0), size (w,h) at position (x,y) must produce the same four corners as a
            // plain axis-aligned (x, y, w, h) rect, so the rotated overload composes with the existing path.
            const float x = 30f, y = 50f, w = 120f, h = 40f;
            var pos = new Vector2(x, y);
            var size = new Vector2(w, h);
            var origin = Vector2.Zero;
            const float cos = 1f, sin = 0f;

            Assert.Equal(new Vector2(x, y), SpriteBatch.RotatedCorner(0f, 0f, pos, size, origin, cos, sin));
            Assert.Equal(new Vector2(x + w, y), SpriteBatch.RotatedCorner(1f, 0f, pos, size, origin, cos, sin));
            Assert.Equal(new Vector2(x + w, y + h), SpriteBatch.RotatedCorner(1f, 1f, pos, size, origin, cos, sin));
            Assert.Equal(new Vector2(x, y + h), SpriteBatch.RotatedCorner(0f, 1f, pos, size, origin, cos, sin));
        }

        [Fact]
        public void RotatedCorner_PivotLandsAtPositionRegardlessOfRotation()
        {
            // The normalized origin corner always lands exactly on `position` for any rotation.
            var pos = new Vector2(200f, 100f);
            var size = new Vector2(64f, 16f);
            var origin = new Vector2(0f, 0.5f);        // the DrawLine origin (left edge, vertical middle)
            float angle = System.MathF.PI / 3f;        // 60 degrees
            float cos = System.MathF.Cos(angle), sin = System.MathF.Sin(angle);

            Vector2 pivot = SpriteBatch.RotatedCorner(origin.X, origin.Y, pos, size, origin, cos, sin);
            Assert.Equal(pos.X, pivot.X, Eps);
            Assert.Equal(pos.Y, pivot.Y, Eps);
        }

        [Fact]
        public void RotatedCorner_NinetyDegrees_RotatesEdgeIntoYAxis()
        {
            // A horizontal edge of length L rotated +90deg (screen-space CW, y-down) points along +Y.
            var pos = new Vector2(0f, 0f);
            var size = new Vector2(10f, 2f);
            var origin = new Vector2(0f, 0.5f);
            float angle = System.MathF.PI / 2f;
            float cos = System.MathF.Cos(angle), sin = System.MathF.Sin(angle);

            // The far end of the line (normalized x=1 at the centerline) sits at length L along the rotated axis.
            Vector2 far = SpriteBatch.RotatedCorner(1f, 0.5f, pos, size, origin, cos, sin);
            Assert.Equal(0f, far.X, Eps);
            Assert.Equal(10f, far.Y, Eps);
        }
    }
}
