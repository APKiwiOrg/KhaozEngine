using System;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless geometry tests for the partial-arc stroke helpers on <see cref="PrimitiveRenderer"/>:
    /// the radial-progress sweep (clamp + direction sign) and the arc-adaptive segment count (scales
    /// with the arc's fraction of a full turn, floored at 1, zero when segments are non-positive).
    /// The visual stroke is exercised by the same rotated-quad path as <see cref="PrimitiveRenderer.DrawRing"/>.
    /// </summary>
    public class PrimitiveArcTests
    {
        const float Eps = 1e-4f;

        // --- RadialProgressSweep (clamp fraction to [0,1], sign follows direction) -------------------

        [Theory]
        [InlineData(-0.5f, 0f)]   // below zero clamps to no sweep
        [InlineData(0f, 0f)]      // empty
        [InlineData(0.25f, MathF.Tau / 4f)]
        [InlineData(0.5f, MathF.PI)]
        [InlineData(1f, MathF.Tau)]  // full ring
        [InlineData(2f, MathF.Tau)]  // above one clamps to a full ring
        public void RadialProgressSweep_ClampsFractionAndMapsToTau(float fraction, float expected)
        {
            Assert.Equal(expected, PrimitiveRenderer.RadialProgressSweep(fraction, clockwise: true), Eps);
        }

        [Fact]
        public void RadialProgressSweep_CounterClockwiseNegatesTheSweep()
        {
            // Same magnitude, opposite sign: clockwise sweeps +, counter-clockwise sweeps -.
            float cw = PrimitiveRenderer.RadialProgressSweep(0.25f, clockwise: true);
            float ccw = PrimitiveRenderer.RadialProgressSweep(0.25f, clockwise: false);
            Assert.Equal(MathF.Tau / 4f, cw, Eps);
            Assert.Equal(-MathF.Tau / 4f, ccw, Eps);
        }

        // --- ArcSegments (scales with |sweep| / full turn, floored at 1, 0 when segments <= 0) --------

        [Theory]
        [InlineData(64, MathF.Tau, 64)]        // full turn draws every segment
        [InlineData(64, MathF.Tau / 4f, 16)]   // a quarter arc draws a quarter of them
        [InlineData(64, MathF.PI, 32)]         // a half arc, half the segments
        [InlineData(64, MathF.Tau * 2f, 128)]  // over a full turn scales past `segments`
        public void ArcSegments_ScaleWithArcFractionOfAFullTurn(int segments, float sweep, int expected)
        {
            Assert.Equal(expected, PrimitiveRenderer.ArcSegments(segments, sweep));
        }

        [Fact]
        public void ArcSegments_UseAbsoluteSweepSoDirectionDoesNotMatter()
        {
            Assert.Equal(
                PrimitiveRenderer.ArcSegments(64, MathF.Tau / 4f),
                PrimitiveRenderer.ArcSegments(64, -MathF.Tau / 4f));
        }

        [Theory]
        [InlineData(64, 0.001f)]   // a tiny arc still gets at least one segment
        [InlineData(64, 0f)]       // and a zero sweep floors to one (draws a zero-length nothing)
        public void ArcSegments_FloorAtOneForTinySweeps(int segments, float sweep)
        {
            Assert.Equal(1, PrimitiveRenderer.ArcSegments(segments, sweep));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-8)]
        public void ArcSegments_NonPositiveSegmentsDrawNothing(int segments)
        {
            Assert.Equal(0, PrimitiveRenderer.ArcSegments(segments, MathF.Tau));
        }
    }
}
