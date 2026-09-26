using System.Linq;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D;

public sealed class ColoredTextLayoutTests
{
    static readonly FixedFont Font = new();
    static readonly Vector4 Base = new(1f, 0.9f, 0.3f, 1f);
    static readonly Vector4 Accent = new(0.4f, 0.6f, 1f, 1f);

    [Fact]
    public void Wrap_projects_each_line_back_onto_the_source_colours()
    {
        ColoredTextRun[] source =
        [
            new("Buy ", Base),
            new("Stone pickaxe", Accent),
            new(" (x5)", Base),
        ];

        var lines = ColoredTextLayout.Wrap(Font, source, 100f, hardBreak: true);

        Assert.Equal(["Buy Stone", "pickaxe", "(x5)"], lines.Select(line => line.Text));
        Assert.Equal([Base, Accent], lines[0].Runs.ToArray().Select(run => run.Color));
        Assert.All(lines[1].Runs.ToArray(), run => Assert.Equal(Accent, run.Color));
        Assert.All(lines[2].Runs.ToArray(), run => Assert.Equal(Base, run.Color));
        Assert.All(lines, line => Assert.Equal(line.Text, string.Concat(line.Runs.ToArray().Select(run => run.Text))));
    }

    [Fact]
    public void Hard_break_keeps_every_piece_of_one_token_in_its_source_colour()
    {
        ColoredTextRun[] source =
        [
            new("Equip ", Base),
            new("LongUnbreakableItem", Accent),
        ];

        var lines = ColoredTextLayout.Wrap(Font, source, 60f, hardBreak: true);

        Assert.True(lines.Count > 3);
        Assert.All(lines.Skip(1).SelectMany(line => line.Runs.ToArray()),
            run => Assert.Equal(Accent, run.Color));
    }

    [Fact]
    public void Repeated_text_after_an_explicit_blank_line_uses_the_later_source_run()
    {
        ColoredTextRun[] source =
        [
            new("red\n\n", Base),
            new("red", Accent),
        ];

        var lines = ColoredTextLayout.Wrap(Font, source, 200f);

        Assert.Equal(["red", "", "red"], lines.Select(line => line.Text));
        Assert.Equal(Base, Assert.Single(lines[0].Runs.ToArray()).Color);
        Assert.Empty(lines[1].Runs.ToArray());
        Assert.Equal(Accent, Assert.Single(lines[2].Runs.ToArray()).Color);
    }

    [Fact]
    public void Interior_space_runs_are_preserved_with_their_source_colours()
    {
        ColoredTextRun[] source =
        [
            new("a  ", Base),
            new("b", Accent),
        ];

        var line = Assert.Single(ColoredTextLayout.Wrap(Font, source, 40f));

        Assert.Equal("a  b", line.Text);
        Assert.Equal("a  b", string.Concat(line.Runs.ToArray().Select(run => run.Text)));
    }

    sealed class FixedFont : ITextMeasurer
    {
        public float LineHeight => 20f;
        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);
    }
}
