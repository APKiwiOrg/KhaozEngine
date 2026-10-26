using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

[Collection("AmbientLocalization")]
public sealed class TooltipRichWrappingTests
{
    sealed class FixedFont(float width, float height) : ITextMeasurer
    {
        public float LineHeight => height;
        public Vector2 Measure(string text) => new(text.Length * width, height);
    }

    static readonly FixedFont BodyFont = new(10f, 20f);
    static readonly FixedFont TitleFont = new(12f, 30f);
    static readonly TooltipMetrics Metrics = TooltipMetrics.Default;

    [Fact]
    public void Wrapped_icon_draws_once_with_a_hanging_text_inset()
    {
        var line = new TooltipLine("one two", Vector4.One)
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            GapBefore = 7f,
        };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, 80f);

        Assert.Equal(new[] { "one", "two" }, layout.Lines.Select(row => row.Line.Text));
        Assert.Equal(54f, layout.Width);
        Assert.Equal(50f, layout.Height);
        Assert.Equal(new Vector2(24, 7), layout.Lines[0].TextPosition);
        Assert.Equal(new Vector2(24, 30), layout.Lines[1].TextPosition);
        Assert.Equal(new Rect(0, 9, 16, 16), layout.Lines[0].IconBounds!.Value);
        Assert.Equal(line.Icon, layout.Lines[0].Line.Icon);
        Assert.Null(layout.Lines[1].IconBounds);
        Assert.Null(layout.Lines[1].Line.Icon);
        Assert.Equal(7f, layout.Lines[0].Line.GapBefore);
        Assert.Equal(0f, layout.Lines[1].Line.GapBefore);

        Rect bounds = Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
            new(300, 200), new(960, 540), Metrics, maxWidth: 100f);

        Assert.Equal(new Vector2(74, 66), new Vector2(bounds.Width, bounds.Height));
    }

    [Fact]
    public void Wrapped_title_face_and_gap_survive_once()
    {
        var line = new TooltipLine("one two", Vector4.One)
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            TitleFont = true,
            GapBefore = 7f,
        };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, 80f);

        Assert.Equal(new[] { "one", "two" }, layout.Lines.Select(row => row.Line.Text));
        Assert.Equal(60f, layout.Width);
        Assert.Equal(70f, layout.Height);
        Assert.Equal(new Vector2(24, 7), layout.Lines[0].TextPosition);
        Assert.Equal(new Vector2(24, 40), layout.Lines[1].TextPosition);
        Assert.All(layout.Lines, row => Assert.True(row.Line.TitleFont));
        Assert.Equal(line.Icon, layout.Lines[0].Line.Icon);
        Assert.Null(layout.Lines[1].Line.Icon);
        Assert.Equal(7f, layout.Lines[0].Line.GapBefore);
        Assert.Equal(0f, layout.Lines[1].Line.GapBefore);

        Rect bounds = Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
            new(300, 200), new(960, 540), Metrics, maxWidth: 100f);

        Assert.Equal(new Vector2(80, 86), new Vector2(bounds.Width, bounds.Height));
    }

    [Fact]
    public void Coloured_runs_survive_rich_wrapping()
    {
        Vector4 yellow = new(1f, 0.9f, 0.3f, 1f);
        Vector4 blue = new(0.4f, 0.6f, 1f, 1f);
        TooltipLine line = TooltipLine.OfSegments([
            new LabelSegment(LocalizedText.Raw("one "), yellow),
            new LabelSegment(LocalizedText.Raw("two"), blue),
        ], Vector4.One) with
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            TitleFont = true,
            GapBefore = 7f,
        };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, 80f);

        Assert.Equal(new[] { "one", "two" }, layout.Lines.Select(row => row.Line.Text));
        Assert.All(layout.Lines, row =>
        {
            Assert.Equal(row.Line.Text, string.Concat(row.Line.Runs.ToArray().Select(run => run.Text)));
            Assert.True(row.Line.TitleFont);
        });
        Assert.Equal(yellow, Assert.Single(layout.Lines[0].Line.Runs.ToArray()).Color);
        Assert.Equal(blue, Assert.Single(layout.Lines[1].Line.Runs.ToArray()).Color);
        Assert.Equal(line.Icon, layout.Lines[0].Line.Icon);
        Assert.NotNull(layout.Lines[0].IconBounds);
        Assert.Null(layout.Lines[1].Line.Icon);
        Assert.Null(layout.Lines[1].IconBounds);
        Assert.Equal(7f, layout.Lines[0].Line.GapBefore);
        Assert.Equal(0f, layout.Lines[1].Line.GapBefore);
        Assert.Equal(new Vector2(24, 7), layout.Lines[0].TextPosition);
        Assert.Equal(new Vector2(24, 40), layout.Lines[1].TextPosition);
    }

    [Fact]
    public void Scaled_text_reserves_unscaled_icon_space()
    {
        var line = new TooltipLine("one two", Vector4.One, 2f)
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            GapBefore = 7f,
        };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, 104f);

        Assert.Equal(new[] { "one", "two" }, layout.Lines.Select(row => row.Line.Text));
        Assert.Equal(84f, layout.Width);
        Assert.Equal(90f, layout.Height);
        Assert.All(layout.Lines, row => Assert.Equal(2f, row.Line.Scale));
        Assert.Equal(new Vector2(24, 7), layout.Lines[0].TextPosition);
        Assert.Equal(new Vector2(24, 50), layout.Lines[1].TextPosition);
        Assert.Equal(new Rect(0, 19, 16, 16), layout.Lines[0].IconBounds!.Value);
        Assert.Null(layout.Lines[1].IconBounds);

        Rect bounds = Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
            new(300, 200), new(960, 540), Metrics, maxWidth: 124f);

        Assert.Equal(new Vector2(104, 106), new Vector2(bounds.Width, bounds.Height));
    }

    [Theory]
    [InlineData(24f)]
    [InlineData(23f)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Cap_must_leave_a_positive_icon_text_budget(float contentCap)
    {
        var line = new TooltipLine("one two", Vector4.One)
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            GapBefore = 7f,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, contentCap));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
                new(300, 200), new(960, 540), Metrics, maxWidth: contentCap + 20f));
    }
}
