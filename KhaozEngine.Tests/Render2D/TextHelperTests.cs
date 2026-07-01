using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Render2D
{
    public class TextHelperTests
    {
        // Fake measurer: every char is 10px wide, line height 20px. No GPU device needed.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();

        [Fact]
        public void CenteredX_puts_the_text_midpoint_on_the_anchor()
        {
            // "abc" = 30px wide -> starts 15px left of centerX 100
            Assert.Equal(85f, TextHelper.CenteredX(Font, "abc", centerX: 100f));
        }

        [Fact]
        public void RightX_puts_the_right_edge_on_the_anchor()
        {
            // "abc" = 30px -> starts at 300-30 = 270
            Assert.Equal(270f, TextHelper.RightX(Font, "abc", rightX: 300f));
        }

        [Fact]
        public void CenteredInRect_centers_on_both_axes()
        {
            // rect (10,20,200,60); "abcd" = 40px wide, 20px tall
            // x = 10 + (200-40)/2 = 90 ; y = 20 + (60-20)/2 = 40
            Vector2 p = TextHelper.CenteredInRect(Font, "abcd", new Rect(10f, 20f, 200f, 60f));
            Assert.Equal(90f, p.X);
            Assert.Equal(40f, p.Y);
        }

        [Fact]
        public void MeasureWrappedHeight_matches_TextLayout()
        {
            // wraps "aaaaa bbbbb ccccc ddddd" to 2 lines at width 120 -> 2 * 20px
            Assert.Equal(40f, TextHelper.MeasureWrappedHeight(Font, "aaaaa bbbbb ccccc ddddd", 120f));
        }

        [Fact]
        public void CenteredX_of_empty_text_is_the_anchor()
        {
            Assert.Equal(50f, TextHelper.CenteredX(Font, "", centerX: 50f));
        }
    }
}
