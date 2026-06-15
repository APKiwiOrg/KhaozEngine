using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class TextLayoutTests
    {
        // Fake measurer: every char is 10px wide, line height 20px. No GPU device needed.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();

        [Fact]
        public void AlignedX_left_is_the_left_edge()
        {
            Assert.Equal(100f, TextLayout.AlignedX(Font, "abc", left: 100, width: 200, TextAlign.Left));
        }

        [Fact]
        public void AlignedX_center_offsets_by_half_the_leftover()
        {
            // "abc" = 30px wide in a 200px region -> (200-30)/2 = 85 past the left edge
            Assert.Equal(100f + 85f, TextLayout.AlignedX(Font, "abc", left: 100, width: 200, TextAlign.Center));
        }

        [Fact]
        public void AlignedX_right_puts_the_text_against_the_right_edge()
        {
            // right edge = 100+200 = 300; "abc" 30px -> starts at 270
            Assert.Equal(270f, TextLayout.AlignedX(Font, "abc", left: 100, width: 200, TextAlign.Right));
        }

        [Fact]
        public void Wrap_breaks_lines_at_the_width_limit()
        {
            // each word 5 chars = 50px; maxWidth 120 fits two words ("aaaaa bbbbb" = 110px) but not three
            var lines = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);
            Assert.Equal(2, lines.Count);
            Assert.Equal("aaaaa bbbbb", lines[0]);
            Assert.Equal("ccccc ddddd", lines[1]);
        }

        [Fact]
        public void Wrap_keeps_a_word_wider_than_the_limit_on_its_own_line()
        {
            var lines = TextLayout.Wrap(Font, "tiny enormouslylongword tiny", 60f);
            Assert.Equal(3, lines.Count);
            Assert.Equal("tiny", lines[0]);
            Assert.Equal("enormouslylongword", lines[1]);
            Assert.Equal("tiny", lines[2]);
        }

        [Fact]
        public void Wrap_of_empty_text_yields_no_lines()
        {
            Assert.Empty(TextLayout.Wrap(Font, "", 100f));
        }

        [Fact]
        public void MeasureWrappedHeight_is_line_count_times_line_height()
        {
            // wraps to 2 lines * 20px
            Assert.Equal(40f, TextLayout.MeasureWrappedHeight(Font, "aaaaa bbbbb ccccc ddddd", 120f));
        }
    }
}
