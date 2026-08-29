using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// #82: an explicit line break in the text forces a line break in the wrap. Before this, the word scanner
    /// split on the space character only, so a '\n' was an ordinary word character and got glued onto whatever
    /// word touched it, which also under-reported <see cref="TextLayout.MeasureWrappedHeight"/> for any text
    /// carrying breaks.
    /// </summary>
    public class TextLayoutNewlineTests
    {
        // Same fake measurer the other layout tests use: every char 10px wide, line height 20px.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();

        // Wide enough that nothing wraps on width, so only the explicit breaks are in play.
        const float Wide = 1000f;

        [Fact]
        public void Wrap_BreaksAtAnExplicitNewline()
        {
            var lines = TextLayout.Wrap(Font, "Line one.\nLine two.", Wide);

            Assert.Equal(new[] { "Line one.", "Line two." }, lines);
        }

        [Fact]
        public void Wrap_TreatsCrLfAsOneBreak()
        {
            Assert.Equal(new[] { "Line one.", "Line two." }, TextLayout.Wrap(Font, "Line one.\r\nLine two.", Wide));
        }

        [Fact]
        public void Wrap_KeepsTheEmptyLineBetweenConsecutiveBreaks()
        {
            // A blank line between paragraphs is authored content, not whitespace to swallow.
            Assert.Equal(new[] { "a", "", "b" }, TextLayout.Wrap(Font, "a\n\nb", Wide));
            Assert.Equal(new[] { "a", "", "", "b" }, TextLayout.Wrap(Font, "a\r\n\r\n\r\nb", Wide));
        }

        [Fact]
        public void Wrap_KeepsTheEmptyLastLineWhenTextEndsOnABreak()
        {
            // N breaks, N+1 lines, whatever follows the last one.
            Assert.Equal(new[] { "a", "" }, TextLayout.Wrap(Font, "a\n", Wide));
            Assert.Equal(new[] { "", "a" }, TextLayout.Wrap(Font, "\na", Wide));
            Assert.Equal(new[] { "", "" }, TextLayout.Wrap(Font, "\n", Wide));
        }

        [Fact]
        public void Wrap_ConsumesTheSpacesTouchingABreak()
        {
            // A break consumes its whitespace run exactly as a width-forced break does, in both space modes, so
            // no line comes back carrying leading or trailing space it did not ask for.
            Assert.Equal(new[] { "a", "b" }, TextLayout.Wrap(Font, "a \n b", Wide));
            Assert.Equal(new[] { "a", "b" }, TextLayout.Wrap(Font, "a  \n  b", Wide, preserveSpaceRuns: true));
        }

        [Fact]
        public void Wrap_ALoneCarriageReturnIsNotABreak()
        {
            // Deliberate: only '\n' and the "\r\n" pair break. A bare '\r' stays an ordinary character, like
            // every other control character the wrap does not interpret.
            Assert.Equal(new[] { "a\rb" }, TextLayout.Wrap(Font, "a\rb", Wide));
        }

        [Fact]
        public void Wrap_CombinesExplicitBreaksWithWidthWrapping()
        {
            // 120px = 12 chars. "aaaaa bbbbb" (11) fits, "ccccc" is pushed to its own line by the width, and the
            // break then ends that line before "ddddd" ever gets a chance to pack onto it.
            var lines = TextLayout.Wrap(Font, "aaaaa bbbbb ccccc\nddddd", 120f);

            Assert.Equal(new[] { "aaaaa bbbbb", "ccccc", "ddddd" }, lines);
        }

        [Fact]
        public void Wrap_HardBreakStillSlicesAnOverWideWordAroundABreak()
        {
            var lines = TextLayout.Wrap(Font, "tiny\nenormouslylongword tiny", 60f, hardBreak: true);

            Assert.Equal(new[] { "tiny", "enormo", "uslylo", "ngword", "tiny" }, lines);
        }

        [Fact]
        public void MeasureWrappedHeight_countsTheLinesTheBreaksAdd()
        {
            // The under-reported height in #82: three lines of 20px, not one.
            Assert.Equal(60f, TextLayout.MeasureWrappedHeight(Font, "one\ntwo\nthree", Wide));
        }

        // -- The regression pin. Text with NO break in it must wrap exactly as it did before #82, character for
        // character, in every mode. The expectations below were captured by running this same theory against the
        // pre-#82 implementation, so a drift in the untouched path shows up here rather than in a golden. --

        static string Canonical(List<string> lines) => $"[{lines.Count}] {string.Join("|", lines)}";

        [Theory]
        [InlineData("aaaaa bbbbb ccccc ddddd", 120f, false, false, "[2] aaaaa bbbbb|ccccc ddddd")]
        [InlineData("tiny enormouslylongword tiny", 60f, false, false, "[3] tiny|enormouslylongword|tiny")]
        [InlineData("tiny enormouslylongword tiny", 60f, true, false, "[5] tiny|enormo|uslylo|ngword|tiny")]
        [InlineData("a b c d e f g h", 50f, false, false, "[3] a b c|d e f|g h")]
        [InlineData("solitary", 5f, false, false, "[1] solitary")]
        [InlineData("solitary", 5f, true, false, "[8] s|o|l|i|t|a|r|y")]
        [InlineData("  leading and   interior   spaces  ", 200f, false, false, "[2] leading and interior|spaces")]
        [InlineData("  leading and   interior   spaces  ", 200f, false, true, "[2] leading and|interior   spaces")]
        [InlineData("xx  yy zzzzz", 60f, false, true, "[2] xx  yy|zzzzz")]
        [InlineData("aaaaa  bbbbb", 60f, false, true, "[2] aaaaa|bbbbb")]
        [InlineData("", 100f, false, false, "[0] ")]
        [InlineData("   ", 100f, false, false, "[0] ")]
        [InlineData("word\tstill one token", 200f, false, false, "[1] word\tstill one token")]
        public void Wrap_TextWithoutBreaks_IsUnchangedByTheNewlineSupport(
            string text, float maxWidth, bool hardBreak, bool preserveSpaceRuns, string expected)
        {
            Assert.Equal(expected, Canonical(TextLayout.Wrap(Font, text, maxWidth, hardBreak, preserveSpaceRuns)));
        }
    }
}
