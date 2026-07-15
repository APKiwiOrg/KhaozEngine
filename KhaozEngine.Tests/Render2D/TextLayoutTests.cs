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
        public void Wrap_hard_breaks_a_word_wider_than_the_limit_when_enabled()
        {
            // maxWidth 60 = 6 chars. "enormouslylongword" (18) has no break point, so with hardBreak it is
            // sliced into three 6-char chunks; the trailing "ngword" chunk cannot repack with the following
            // "tiny" (would overflow), so "tiny" starts its own line.
            var lines = TextLayout.Wrap(Font, "tiny enormouslylongword tiny", 60f, hardBreak: true);
            Assert.Equal(new[] { "tiny", "enormo", "uslylo", "ngword", "tiny" }, lines);
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

        // -- Wrap()'s memo cache: cached calls must return the same result as an uncached (first) call, and
        // must never leak between different (font, text, maxWidth, hardBreak) keys. --

        // A second measurer with a different char width, so two calls that share (text, maxWidth) but differ
        // only by font identity must NOT share a cache entry - if they did, one font's result would leak into
        // the other's and this test would catch it as a wrong line count / wrong split point.
        sealed class WideFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 20f, 20f);
        }
        static readonly WideFont Wide = new();

        [Fact]
        public void Wrap_RepeatedCall_SameKey_ReturnsEqualResult_ToTheFirstCall()
        {
            var first = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);
            var second = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);   // hits the memo cache

            Assert.Equal(first, second);
            Assert.NotSame(first, second);   // a fresh list every call - a caller mutating one can't corrupt the cache
        }

        [Fact]
        public void Wrap_RepeatedCall_MutatingTheReturnedList_DoesNotAffectLaterCalls()
        {
            var first = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);
            first.Clear();
            first.Add("corrupted");

            var second = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);

            Assert.Equal(new[] { "aaaaa bbbbb", "ccccc ddddd" }, second);
        }

        [Fact]
        public void Wrap_DifferentMaxWidth_DoesNotReuseAStaleCacheEntry()
        {
            // Same text, three different widths, called out of order (120, 60, 120 again) so a naive
            // last-write-wins cache bug would surface as the third call returning the second call's result.
            var wide = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);
            var narrow = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 60f);
            var wideAgain = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);

            Assert.Equal(2, wide.Count);
            Assert.Equal(4, narrow.Count);   // 60px = one word (50px) per line
            Assert.Equal(wide, wideAgain);
        }

        [Fact]
        public void Wrap_DifferentFontIdentity_DoesNotShareACacheEntryWithTheSameTextAndWidth()
        {
            // Font: 10px/char, so "aaaaa bbbbb" (11 chars incl. space) = 110px, fits 120.
            var narrowFontResult = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f);
            // Wide: 20px/char, so even one 5-char word (100px) barely fits 120, two words never will.
            var wideFontResult = TextLayout.Wrap(Wide, "aaaaa bbbbb ccccc ddddd", 120f);

            Assert.Equal(new[] { "aaaaa bbbbb", "ccccc ddddd" }, narrowFontResult);
            Assert.Equal(new[] { "aaaaa", "bbbbb", "ccccc", "ddddd" }, wideFontResult);

            // Re-querying the first key afterwards must still return its own (unpolluted) result.
            Assert.Equal(narrowFontResult, TextLayout.Wrap(Font, "aaaaa bbbbb ccccc ddddd", 120f));
        }

        [Fact]
        public void Wrap_DifferentHardBreak_DoesNotShareACacheEntryWithTheSameTextWidthAndFont()
        {
            var noHardBreak = TextLayout.Wrap(Font, "tiny enormouslylongword tiny", 60f, hardBreak: false);
            var withHardBreak = TextLayout.Wrap(Font, "tiny enormouslylongword tiny", 60f, hardBreak: true);

            Assert.Equal(3, noHardBreak.Count);
            Assert.Equal(new[] { "tiny", "enormo", "uslylo", "ngword", "tiny" }, withHardBreak);
        }

        [Theory]
        [InlineData("aaaaa bbbbb ccccc ddddd", 120f)]
        [InlineData("tiny enormouslylongword tiny", 60f)]
        [InlineData("a b c d e f g h", 50f)]
        [InlineData("", 100f)]
        [InlineData("solitary", 5f)]
        public void Wrap_CachedResult_MatchesRecomputedResult_ForAMatrixOfInputs(string text, float maxWidth)
        {
            // First call populates the cache; a distinct, unrelated call in between forces at least one other
            // key through the same cache before we re-query, so this cannot pass by coincidentally hitting an
            // empty cache both times.
            var populate = TextLayout.Wrap(Font, text, maxWidth);
            _ = TextLayout.Wrap(Font, "unrelated filler text to occupy the cache", 200f);
            var cached = TextLayout.Wrap(Font, text, maxWidth);

            Assert.Equal(populate, cached);
        }
    }
}
