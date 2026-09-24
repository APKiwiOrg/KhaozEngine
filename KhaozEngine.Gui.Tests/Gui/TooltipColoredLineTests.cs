using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TooltipColoredLineTests
{
    sealed class FixedFont : ITextMeasurer
    {
        public float LineHeight => 20f;
        public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
    }

    static readonly FixedFont Font = new();
    static readonly Vector4 Yellow = new(1f, 0.9f, 0.3f, 1f);
    static readonly Vector4 Rare = new(0.4f, 0.6f, 1f, 1f);

    [Fact]
    public void LocalizedSegmentsRetainTheirOrderAndColours()
    {
        TooltipLine line = TooltipLine.OfSegments([
            new LabelSegment(LocalizedText.Raw("Buy "), Yellow),
            new LabelSegment(LocalizedText.Raw("Stone pickaxe"), Rare),
            new LabelSegment(LocalizedText.Raw(" (x5)"), Yellow),
        ], Yellow);

        Assert.Equal("Buy Stone pickaxe (x5)", line.Text);
        Assert.Equal([Yellow, Rare, Yellow], line.Runs.ToArray().Select(run => run.Color));
        Assert.Equal(line.Text, string.Concat(line.Runs.ToArray().Select(run => run.Text)));
    }

    [Fact]
    public void WrappedSegmentsKeepTheTargetColourAcrossLineBreaksAndStayInsideTheCap()
    {
        TooltipLine source = TooltipLine.OfSegments([
            new LabelSegment(LocalizedText.Raw("Buy "), Yellow),
            new LabelSegment(LocalizedText.Raw("Stone pickaxe"), Rare),
            new LabelSegment(LocalizedText.Raw(" (x5)"), Yellow),
        ], Yellow);

        var bounds = Tooltip.ComputeBounds(Font, "", "", Font, Font, [source],
            new Vector2(200, 200), new Vector2(600, 400), TooltipMetrics.Default,
            100f, 1f, TooltipAnchorMode.Offset, out var visual);

        Assert.True(bounds.Width <= 100f);
        Assert.True(visual.Count > 1);
        Assert.All(visual, line =>
            Assert.Equal(line.Text, string.Concat(line.Runs.ToArray().Select(run => run.Text))));
        Assert.Contains(visual.SelectMany(line => line.Runs.ToArray()), run =>
            run.Text.Contains("Stone", StringComparison.Ordinal) && run.Color == Rare);
        Assert.Contains(visual.SelectMany(line => line.Runs.ToArray()), run =>
            run.Text.Contains("pickaxe", StringComparison.Ordinal) && run.Color == Rare);
        Assert.Contains(visual.SelectMany(line => line.Runs.ToArray()), run =>
            run.Text.Contains("(x5)", StringComparison.Ordinal) && run.Color == Yellow);
    }

    [Fact]
    public void HardBrokenItemNameRetainsItsColourOnEveryPiece()
    {
        TooltipLine source = TooltipLine.OfSegments([
            new LabelSegment(LocalizedText.Raw("Equip "), Yellow),
            new LabelSegment(LocalizedText.Raw("LongUnbreakableItem"), Rare),
        ], Yellow);

        _ = Tooltip.ComputeBounds(Font, "", "", Font, Font, [source],
            new Vector2(200, 200), new Vector2(600, 400), TooltipMetrics.Default,
            90f, 1f, TooltipAnchorMode.Offset, out var visual);

        Assert.True(visual.Count > 2);
        Assert.All(visual.SelectMany(line => line.Runs.ToArray())
            .Where(run => run.Text.Any(char.IsLetter) && !run.Text.Contains("Equip", StringComparison.Ordinal)),
            run => Assert.Equal(Rare, run.Color));
    }
}
