using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class TextLayoutPreserveSpacesTests
    {
        // Same fake measurer as the issue's repro: every char is 10px wide, line height 20px. No GPU device needed.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();

        [Fact]
        public void Default_CollapsesInteriorSpaceRun()
        {
            // The proven repro: no wrap occurs (19 chars = 190px < 500), but the double space is collapsed to one.
            var lines = TextLayout.Wrap(Font, "Alice: hello  world", 500f);
            Assert.Single(lines);
            Assert.Equal("Alice: hello world", lines[0]);
        }

        [Fact]
        public void Preserve_KeepsInteriorSpaceRunVerbatim()
        {
            // Same string, preserve mode on: the double space survives, still no wrap.
            var lines = TextLayout.Wrap(Font, "Alice: hello  world", 500f, preserveSpaceRuns: true);
            Assert.Single(lines);
            Assert.Equal("Alice: hello  world", lines[0]);
        }

        [Fact]
        public void Preserve_RunOnANonBreakingLine_ButWrapTakenLater()
        {
            // "xx  yy zzzzz": the double space packs onto line 0 verbatim; the single space before the long word is a
            // break point that pushes it to line 1 (which carries no leading spaces).
            var lines = TextLayout.Wrap(Font, "xx  yy zzzzz", 60f, preserveSpaceRuns: true);
            Assert.Equal(2, lines.Count);
            Assert.Equal("xx  yy", lines[0]);
            Assert.Equal("zzzzz", lines[1]);
        }

        [Fact]
        public void Preserve_BreakTakenAtMultiSpaceRun_ConsumesIt()
        {
            // "aaaaa  bbbbb" is 12 chars = 120px > 60, so a break is taken AT the double-space run. The run is
            // consumed: the fresh line starts with "bbbbb", no leading spaces - matching the default break behaviour.
            var lines = TextLayout.Wrap(Font, "aaaaa  bbbbb", 60f, preserveSpaceRuns: true);
            Assert.Equal(2, lines.Count);
            Assert.Equal("aaaaa", lines[0]);
            Assert.Equal("bbbbb", lines[1]);
        }

        [Theory]
        // The whole default-mode matrix must be UNCHANGED by the new parameter: every case collapses runs exactly as
        // before. Each pair is (input, expected single/joined default output).
        [InlineData("Alice: hello  world", "Alice: hello world")]
        [InlineData("a  b   c", "a b c")]
        [InlineData("  leading and trailing  ", "leading and trailing")]
        [InlineData("single space", "single space")]
        public void Default_MatrixUnchanged_CollapsesEverything(string input, string expectedJoined)
        {
            var lines = TextLayout.Wrap(Font, input, 1000f);   // wide enough that nothing wraps
            Assert.Single(lines);
            Assert.Equal(expectedJoined, lines[0]);
        }

        [Fact]
        public void Preserve_SingleSpaces_MatchDefault()
        {
            // With no runs longer than one, preserve mode is identical to default.
            var d = TextLayout.Wrap(Font, "one two three four", 1000f);
            var p = TextLayout.Wrap(Font, "one two three four", 1000f, preserveSpaceRuns: true);
            Assert.Equal(d, p);
            Assert.Equal("one two three four", p[0]);
        }

        [Fact]
        public void CacheKey_SeparatesModes_EitherOrder()
        {
            const string s = "Alice: hello  world";

            // Off, then on, then off, then on: each returns its own correct result, never a poisoned cache hit.
            Assert.Equal("Alice: hello world", TextLayout.Wrap(Font, s, 500f)[0]);
            Assert.Equal("Alice: hello  world", TextLayout.Wrap(Font, s, 500f, preserveSpaceRuns: true)[0]);
            Assert.Equal("Alice: hello world", TextLayout.Wrap(Font, s, 500f)[0]);
            Assert.Equal("Alice: hello  world", TextLayout.Wrap(Font, s, 500f, preserveSpaceRuns: true)[0]);

            // And the reverse first-seen order for a fresh string, to prove neither ordering poisons the other.
            const string t = "foo  bar  baz";
            Assert.Equal("foo  bar  baz", TextLayout.Wrap(Font, t, 500f, preserveSpaceRuns: true)[0]);
            Assert.Equal("foo bar baz", TextLayout.Wrap(Font, t, 500f)[0]);
        }

        [Fact]
        public void MeasureWrappedHeight_ForwardsMode()
        {
            // A preserved run can force a wrap the collapsed form avoids, so the reported height differs.
            // "aa  bb": collapsed "aa bb" = 5 chars = 50px fits in 55 (one line); preserved "aa  bb" = 6 chars = 60px
            // does not, so it wraps to two lines.
            Assert.Equal(20f, TextLayout.MeasureWrappedHeight(Font, "aa  bb", 55f));
            Assert.Equal(40f, TextLayout.MeasureWrappedHeight(Font, "aa  bb", 55f, preserveSpaceRuns: true));
        }
    }
}
