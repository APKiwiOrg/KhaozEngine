using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class TooltipTests
    {
        // 10px/char, 20px line height.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly TooltipMetrics M = TooltipMetrics.Default;

        static TooltipLine[] One(string s) => new[] { new TooltipLine(s, Vector4.One) };

        [Fact]
        public void Auto_sizes_to_content_and_sits_above_the_anchor()
        {
            // title "Hello"(50) / line "World"(50): contentW=50 -> panelW=70.
            // contentH = (20+5) + (20+3) - 3 = 45 -> panelH = 45+16 = 61.
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(300, 200), View, M);
            Assert.Equal(70f, r.Width);
            Assert.Equal(61f, r.Height);
            Assert.Equal(300f - 35f, r.X);          // centered on anchor X
            Assert.Equal(200f - 61f - 10f, r.Y);    // above anchor (anchorOffset 10)
        }

        [Fact]
        public void Flips_below_the_anchor_when_there_is_no_room_above()
        {
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(300, 30), View, M);
            Assert.Equal(30f + 10f, r.Y);           // flipped: anchor.Y + anchorOffset
        }

        [Fact]
        public void Clamps_horizontally_into_the_viewport()
        {
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(10, 200), View, M);
            Assert.Equal(4f, r.X);                  // clamped to left margin
        }

        [Fact]
        public void Width_follows_the_widest_line()
        {
            // longest body line 8 chars = 80 -> contentW 80 -> panelW 100
            var lines = new[] { new TooltipLine("ab", Vector4.One), new TooltipLine("longline", Vector4.One) };
            var r = Tooltip.ComputeBounds(Font, "", Font, lines, new Vector2(400, 300), View, M);
            Assert.Equal(100f, r.Width);
        }
    }
}
