using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class TextAreaLayoutTests
    {
        // 10 px per UTF-16 unit, line height 20. Counts every character passed to Measure.
        sealed class CountingMeasurer : ITextMeasurer
        {
            public int MeasuredChars;

            public float LineHeight => 20f;

            public Vector2 Measure(string text) => Measure(text.AsSpan());

            public Vector2 Measure(ReadOnlySpan<char> text)
            {
                MeasuredChars += text.Length;
                return new Vector2(text.Length * 10f, LineHeight);
            }
        }

        static IReadOnlyList<TextAreaLine> Wrap(string text, float width) =>
            TextAreaLayout.Wrap(new CountingMeasurer(), text, width);

        static void AssertLines(IReadOnlyList<TextAreaLine> lines, params (int Start, int Length)[] expected)
        {
            Assert.Equal(expected.Length, lines.Count);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(new TextAreaLine(expected[i].Start, expected[i].Length), lines[i]);
        }

        [Fact]
        public void Newline_ends_a_line() => AssertLines(Wrap("ab\ncd", 100), (0, 2), (3, 2));

        [Fact]
        public void Words_wrap_after_the_last_space() =>
            AssertLines(Wrap("hello world again", 120), (0, 12), (12, 5));

        [Fact]
        public void Long_word_hard_breaks() => AssertLines(Wrap("abcdefghij", 40), (0, 4), (4, 4), (8, 2));

        [Fact]
        public void Trailing_newline_adds_an_empty_line() => AssertLines(Wrap("ab\n", 100), (0, 2), (3, 0));

        [Fact]
        public void Empty_text_is_one_empty_line() => AssertLines(Wrap("", 100), (0, 0));

        [Fact]
        public void Surrogate_pairs_are_never_split()
        {
            IReadOnlyList<TextAreaLine> lines = Wrap("a\U0001F600b", 20);

            Assert.DoesNotContain(lines, l => l.Start == 2);
            AssertLines(lines, (0, 1), (1, 2), (3, 1));
        }

        [Fact]
        public void Caret_at_a_soft_wrap_sits_on_the_next_line()
        {
            var font = new CountingMeasurer();
            const string text = "hello world again";
            IReadOnlyList<TextAreaLine> lines = TextAreaLayout.Wrap(font, text, 120);

            Assert.Equal((1, 0f), TextAreaLayout.CaretPosition(font, text, lines, 12));
        }

        [Fact]
        public void IndexAt_picks_the_nearest_boundary()
        {
            var font = new CountingMeasurer();
            const string text = "abcd";
            IReadOnlyList<TextAreaLine> lines = TextAreaLayout.Wrap(font, text, 100);

            Assert.Equal(1, TextAreaLayout.IndexAt(font, text, lines, 0, 14));
            Assert.Equal(2, TextAreaLayout.IndexAt(font, text, lines, 0, 16));
        }

        [Fact]
        public void IndexAt_stays_on_a_soft_wrapped_line()
        {
            var font = new CountingMeasurer();
            const string text = "hello world again";
            IReadOnlyList<TextAreaLine> lines = TextAreaLayout.Wrap(font, text, 120);

            // Index 12 renders at the start of line 1, so the far end of line 0 resolves to 11.
            Assert.Equal(11, TextAreaLayout.IndexAt(font, text, lines, 0, 500));
            Assert.Equal(17, TextAreaLayout.IndexAt(font, text, lines, 1, 500));
        }

        [Fact]
        public void Wrap_measures_linearly()
        {
            var sb = new StringBuilder();
            string[] words = ["lorem", "ip", "abcdefghijklmnopqrstuvwxy", "dolor", "sit", "amet", "zyxwvutsrqponmlkjihgfedcba"];
            for (int i = 0; sb.Length < 4000; i++)
                sb.Append(words[i % words.Length]).Append(i % 5 == 4 ? "  " : " ");
            string text = sb.ToString(0, 4000);
            var font = new CountingMeasurer();

            IReadOnlyList<TextAreaLine> lines = TextAreaLayout.Wrap(font, text, 120);

            Assert.True(lines.Count > 1);
            Assert.True(font.MeasuredChars <= 2 * text.Length,
                $"measured {font.MeasuredChars} chars for a {text.Length} char text");
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        public void Zero_or_invalid_width_puts_one_scalar_per_line(float width) =>
            AssertLines(Wrap("abc", width), (0, 1), (1, 1), (2, 1));
    }
}
