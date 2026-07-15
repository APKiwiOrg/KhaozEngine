using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    // Pure geometry for the MMO radial cooldown pie (no GPU): GuiDraw.CooldownSweepQuads returns a fan of quads
    // over a rect. On a SQUARE the swept area at the quarter fractions is exactly proportional (each square
    // quadrant / octant boundary lands on a corner), so the area assertions are exact to float precision.
    public class CooldownSweepTests
    {
        static readonly Rect Square = new(0, 0, 100, 100);

        static float FanArea(IReadOnlyList<GuiDraw.CooldownQuad> quads)
        {
            float area = 0f;
            foreach (var q in quads)
            {
                area += TriArea(q.P0, q.P1, q.P2);
                area += TriArea(q.P0, q.P2, q.P3);   // second triangle of the quad (degenerate for a fan slice => ~0)
            }
            return area;
        }

        static float TriArea(Vector2 a, Vector2 b, Vector2 c) =>
            MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) * 0.5f;

        static void AssertOnPerimeter(Vector2 p)
        {
            bool onX = MathF.Abs(p.X - 0f) < 1e-3f || MathF.Abs(p.X - 100f) < 1e-3f;
            bool onY = MathF.Abs(p.Y - 0f) < 1e-3f || MathF.Abs(p.Y - 100f) < 1e-3f;
            Assert.True(onX || onY, $"vertex {p} is not on the rect perimeter");
        }

        [Fact]
        public void Fraction_zero_or_less_is_an_empty_fan()
        {
            Assert.Empty(GuiDraw.CooldownSweepQuads(Square, 0f));
            Assert.Empty(GuiDraw.CooldownSweepQuads(Square, -1f));
        }

        [Theory]
        [InlineData(0.25f, 2500f)]
        [InlineData(0.5f, 5000f)]
        [InlineData(0.75f, 7500f)]
        [InlineData(1.0f, 10000f)]
        public void Fan_area_matches_the_fraction_on_a_square(float fraction, float expected)
        {
            float area = FanArea(GuiDraw.CooldownSweepQuads(Square, fraction));
            Assert.True(MathF.Abs(area - expected) < 1f, $"fraction {fraction}: area {area}, expected {expected}");
        }

        [Fact]
        public void Fraction_above_one_clamps_to_full_coverage()
        {
            float area = FanArea(GuiDraw.CooldownSweepQuads(Square, 5f));
            Assert.True(MathF.Abs(area - 10000f) < 1f);
        }

        [Fact]
        public void Every_slice_apex_is_the_centre_and_the_fixed_edge_ends_at_twelve_oclock()
        {
            var quads = GuiDraw.CooldownSweepQuads(Square, 0.5f);
            Assert.NotEmpty(quads);
            var center = new Vector2(50, 50);
            foreach (var q in quads) Assert.Equal(center, q.P0);   // the fan apex is the rect centre
            // The covered region's fixed edge is the 12 o'clock line: the last slice ends at top-centre.
            Vector2 last = quads[quads.Count - 1].P2;
            Assert.Equal(50f, last.X, 3);
            Assert.Equal(0f, last.Y, 3);
        }

        [Fact]
        public void All_perimeter_vertices_lie_on_the_rect_edge()
        {
            foreach (var q in GuiDraw.CooldownSweepQuads(Square, 0.5f))
            {
                AssertOnPerimeter(q.P1);
                AssertOnPerimeter(q.P2);
            }
        }

        [Fact]
        // Locks the sweep to clockwise direction. A mirrored counterclockwise sweep would produce right-half points like (100, 50).
        public void Quarter_fraction_covers_the_left_side_of_the_sweep_not_the_right()
        {
            var quads = GuiDraw.CooldownSweepQuads(Square, 0.25f);
            Assert.Equal(2, quads.Count);

            // First quad's trailing-edge perimeter point is the left-edge midpoint
            Assert.Equal(0f, quads[0].P1.X, 3);
            Assert.Equal(50f, quads[0].P1.Y, 3);

            // Every vertex lies on the left half (would fail on a mirrored sweep)
            foreach (var q in quads)
            {
                Assert.True(q.P0.X <= 50f + 1e-3f, $"P0.X {q.P0.X} exceeds left half");
                Assert.True(q.P1.X <= 50f + 1e-3f, $"P1.X {q.P1.X} exceeds left half");
                Assert.True(q.P2.X <= 50f + 1e-3f, $"P2.X {q.P2.X} exceeds left half");
                Assert.True(q.P3.X <= 50f + 1e-3f, $"P3.X {q.P3.X} exceeds left half");
            }

            // Wedge passes through the top-left corner
            var vertices = new[] { quads[0].P0, quads[0].P1, quads[0].P2, quads[0].P3,
                                   quads[1].P0, quads[1].P1, quads[1].P2, quads[1].P3 };
            bool hasTopLeftCorner = vertices.Any(v => MathF.Abs(v.X - 0f) < 1e-3f && MathF.Abs(v.Y - 0f) < 1e-3f);
            Assert.True(hasTopLeftCorner, "wedge does not pass through top-left corner (0, 0)");
        }
    }
}
