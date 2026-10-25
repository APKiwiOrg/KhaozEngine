using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TooltipBodyLayoutTests
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
    public void Fractional_scale_bounds_preserve_title_first_accumulation()
    {
        var font = new FixedFont(10f, 20f);
        var lines = new TooltipLine[6];
        Array.Fill(lines, new TooltipLine("body", Vector4.One, 0.33f));

        Rect bounds = Tooltip.ComputeBounds(font, "title", font, lines,
            new(300, 300), new(960, 540), Metrics);

        Assert.Equal(95.59999084472656f, bounds.Height);
    }

    [Fact]
    public void Fractional_scale_drawing_preserves_the_fourth_row_pixel()
    {
        var font = new FixedFont(10f, 14f);
        var lines = new TooltipLine[8];
        Array.Fill(lines, new TooltipLine("body", Vector4.One, 1.1f));
        Rect bounds = Tooltip.ComputeLayout(font, "", "", font, font, lines,
            new(300, 300), new(960, 540), Metrics, float.PositiveInfinity,
            1f, TooltipAnchorMode.Centered, out TooltipBodyLayout body);
        var cursor = new Vector2(bounds.X + Metrics.PadX, bounds.Y + Metrics.PadY);
        TooltipVisualLine fourth = default;

        for (int i = 0; i < 4; i++)
            fourth = body.Lines[i].Place(ref cursor, Metrics.LineSpacing);

        Assert.Equal(160.20001220703125f, bounds.Height);
        Assert.Equal(192.99996948242188f, fourth.TextPosition.Y);
        Assert.Equal(192f, MathF.Floor(fourth.TextPosition.Y));
    }

    [Fact]
    public void Taller_icon_centres_text_and_offsets_its_origin()
    {
        var line = new TooltipLine("ab", Vector4.One) { Icon = new(null!, new(0, 0, 1, 1), 32f) };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, float.PositiveInfinity);

        Assert.Equal(60f, layout.Width);
        Assert.Equal(32f, layout.Height);
        Assert.Equal(new Vector2(40f, 6f), layout.Lines[0].TextPosition);
        Assert.Equal(new Rect(0, 0, 32, 32), layout.Lines[0].IconBounds!.Value);
        Assert.Equal(60f, layout.Lines[0].Width);
        Assert.Equal(32f, layout.Lines[0].Height);

        Rect bounds = Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
            new(300, 200), new(960, 540), Metrics);
        Assert.Equal(new Vector2(80, 48), new Vector2(bounds.Width, bounds.Height));

        line = line with { GapBefore = 7f };
        layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, float.PositiveInfinity);

        Assert.Equal(new Vector2(40, 13), layout.Lines[0].TextPosition);
        Assert.Equal(new Rect(0, 7, 32, 32), layout.Lines[0].IconBounds!.Value);
        Assert.Equal(39f, layout.Height);
        Assert.Equal(32f, layout.Lines[0].Height);
        bounds = Tooltip.ComputeBounds(TitleFont, "", BodyFont, [line],
            new(300, 200), new(960, 540), Metrics);
        Assert.Equal(55f, bounds.Height);
    }

    [Fact]
    public void Title_font_rows_use_the_title_face_but_not_TitleScale()
    {
        var line = new TooltipLine("ab", Vector4.One)
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            TitleFont = true,
        };

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, float.PositiveInfinity);

        Assert.Equal(48f, layout.Width);
        Assert.Equal(30f, layout.Height);
        Assert.Equal(new Vector2(24, 0), layout.Lines[0].TextPosition);
        Assert.Equal(new Rect(0, 7, 16, 16), layout.Lines[0].IconBounds!.Value);

        line = line with { Scale = 0.5f };
        layout = TooltipBodyLayout.Build(TitleFont, BodyFont, [line], Metrics, float.PositiveInfinity);

        Assert.Equal(36f, layout.Width);
        Assert.Equal(16f, layout.Height);
        Assert.Equal(new Vector2(24, 0.5f), layout.Lines[0].TextPosition);
        Assert.Equal(new Rect(0, 0, 16, 16), layout.Lines[0].IconBounds!.Value);

        Rect bounds = Tooltip.ComputeBounds(TitleFont, "", "", BodyFont, BodyFont, [line],
            new(300, 200), new(960, 540), Metrics, float.PositiveInfinity, titleScale: 1f);
        Rect scaledTitle = Tooltip.ComputeBounds(TitleFont, "", "", BodyFont, BodyFont, [line],
            new(300, 200), new(960, 540), Metrics, float.PositiveInfinity, titleScale: 2f);

        Assert.Equal(new Vector2(56, 32), new Vector2(bounds.Width, bounds.Height));
        Assert.Equal(bounds, scaledTitle);
    }

    [Fact]
    public void GapBefore_is_extra_space_without_a_trailing_gap()
    {
        TooltipLine[] lines = [new("a", Vector4.One), new TooltipLine("b", Vector4.One) { GapBefore = 9f }];

        var layout = TooltipBodyLayout.Build(TitleFont, BodyFont, lines, Metrics, float.PositiveInfinity);

        Assert.Equal(10f, layout.Width);
        Assert.Equal(52f, layout.Height);
        Assert.Equal(Vector2.Zero, layout.Lines[0].TextPosition);
        Assert.Equal(new Vector2(0, 32), layout.Lines[1].TextPosition);
        Assert.All(layout.Lines, row =>
        {
            Assert.Null(row.IconBounds);
            Assert.Equal(20f, row.Height);
        });
    }

    [Theory]
    [InlineData(0f, -1f)]
    [InlineData(-1f, -1f)]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, float.NegativeInfinity)]
    public void New_geometry_rejects_nonfinite_or_negative_values(float invalidIconSize, float invalidGap)
    {
        var iconLine = new TooltipLine("ab", Vector4.One)
        {
            Icon = new(null!, new(0, 0, 1, 1), invalidIconSize),
        };
        var gapLine = new TooltipLine("ab", Vector4.One) { GapBefore = invalidGap };
        TooltipMetrics metrics = Metrics;
        metrics.IconGap = invalidGap;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBodyLayout.Build(TitleFont, BodyFont, [iconLine], Metrics, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBodyLayout.Build(TitleFont, BodyFont, [gapLine], Metrics, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBodyLayout.Build(TitleFont, BodyFont, [], metrics, float.PositiveInfinity));
    }
}
