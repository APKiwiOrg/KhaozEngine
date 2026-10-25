using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TooltipVisualDefaultsTests
{
    sealed class FixedFont : ITextMeasurer
    {
        public float LineHeight => 20f;
        public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
    }

    [Fact]
    public void New_visual_options_default_to_legacy_behavior()
    {
        var tip = new Tooltip(null!, null!);
        var line = TooltipLine.Of(LocalizedText.Raw("World"), Vector4.One);

        Assert.Null(tip.Backdrop);
        Assert.Null(line.Icon);
        Assert.False(line.TitleFont);
        Assert.Equal(0f, line.GapBefore);
        Assert.Equal(8f, TooltipMetrics.Default.IconGap);
        Assert.Equal(1f, line.Scale);
        Assert.Equal(new TooltipLine("World", Vector4.One), line);
        Assert.Equal(new TooltipLine("World", Vector4.One, 1f), line);

        var (text, color, scale) = line;
        Assert.Equal("World", text);
        Assert.Equal(Vector4.One, color);
        Assert.Equal(1f, scale);
    }

    [Fact]
    public void Default_compute_bounds_is_identical()
    {
        var font = new FixedFont();
        var line = TooltipLine.Of(LocalizedText.Raw("World"), Vector4.One);

        Rect plain = Tooltip.ComputeBounds(font, "Hello", font, [line],
            new(300, 200), new(960, 540), TooltipMetrics.Default);
        Rect explicitDefaults = Tooltip.ComputeBounds(font, "Hello", "", font, font,
            [line with { Icon = null, TitleFont = false, GapBefore = 0f }],
            new(300, 200), new(960, 540), TooltipMetrics.Default, float.PositiveInfinity, 1f);

        Assert.Equal(new Rect(265, 129, 70, 61), plain);
        Assert.Equal(plain, explicitDefaults);
        Assert.Equal(new Rect(290, 174, 20, 16), Tooltip.ComputeBounds(font, "", font,
            [default(TooltipLine)], new(300, 200), new(960, 540), TooltipMetrics.Default));
    }

    [Fact]
    public void With_initializers_preserve_text_colour_scale_and_runs()
    {
        Vector4 color = new(1f, 0.9f, 0.3f, 1f);
        TooltipLine source = TooltipLine.OfSegments([
            new LabelSegment(LocalizedText.Raw("Equip "), color),
            new LabelSegment(LocalizedText.Raw("Stone pickaxe"), new(0.4f, 0.6f, 1f, 1f)),
        ], color, 0.75f);

        TooltipLine decorated = source with
        {
            Icon = new(null!, new(0, 0, 1, 1), 16f),
            TitleFont = true,
            GapBefore = 7f,
        };

        Assert.False(source.Runs.IsEmpty);
        Assert.Equal(source.Text, decorated.Text);
        Assert.Equal(source.Color, decorated.Color);
        Assert.Equal(source.Scale, decorated.Scale);
        Assert.Equal(source.Runs, decorated.Runs);
        Assert.Equal(new TooltipIcon(null!, new(0, 0, 1, 1), 16f), decorated.Icon);
        Assert.True(decorated.TitleFont);
        Assert.Equal(7f, decorated.GapBefore);

        TooltipLine replaced = decorated with { Text = "New action" };

        Assert.Equal("New action", replaced.Text);
        Assert.True(replaced.Runs.IsEmpty);
        Assert.Equal(decorated.Color, replaced.Color);
        Assert.Equal(decorated.Scale, replaced.Scale);
        Assert.Equal(decorated.Icon, replaced.Icon);
        Assert.Equal(decorated.TitleFont, replaced.TitleFont);
        Assert.Equal(decorated.GapBefore, replaced.GapBefore);
    }
}
