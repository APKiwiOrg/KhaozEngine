using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public class TooltipStackLayoutTests
{
    sealed class FixedFont : ITextMeasurer
    {
        public float LineHeight => 20f;
        public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
    }

    [Fact]
    public void BoxesKeepTheirOrderAndGapBesideThePointer()
    {
        Vector2[] sizes = [new(80, 20), new(120, 40), new(100, 60)];
        Span<Rect> boxes = stackalloc Rect[3];

        Rect group = TooltipStackLayout.Place(sizes, new Vector2(100, 100),
            new Vector2(400, 300), boxes, gap: 4, offset: 12, margin: 4);

        Assert.Equal(new Rect(112, 112, 120, 128), group);
        Assert.Equal(new Rect(112, 112, 80, 20), boxes[0]);
        Assert.Equal(new Rect(112, 136, 120, 40), boxes[1]);
        Assert.Equal(new Rect(112, 180, 100, 60), boxes[2]);
    }

    [Fact]
    public void WholeStackFlipsAtTheViewportEdgesWithoutChangingItsOrder()
    {
        Vector2[] sizes = [new(80, 20), new(120, 40), new(100, 60)];
        Span<Rect> boxes = stackalloc Rect[3];

        Rect group = TooltipStackLayout.Place(sizes, new Vector2(390, 280),
            new Vector2(400, 300), boxes, gap: 4, offset: 12, margin: 4);

        Assert.Equal(new Rect(258, 140, 120, 128), group);
        Assert.Equal(new Rect(258, 140, 80, 20), boxes[0]);
        Assert.Equal(new Rect(258, 164, 120, 40), boxes[1]);
        Assert.Equal(new Rect(258, 208, 100, 60), boxes[2]);
    }

    [Fact]
    public void TooSmallOutputCannotSilentlyDropABox()
    {
        Vector2[] sizes = [new(80, 20), new(120, 40)];
        Rect[] boxes = new Rect[1];
        Assert.Throws<ArgumentException>(() => TooltipStackLayout.Place(sizes,
            new Vector2(100, 100), new Vector2(400, 300), boxes));
    }

    [Fact]
    public void AnchorForMakesAnOffsetTooltipLandInItsAssignedBox()
    {
        var font = new FixedFont();
        Vector2 viewport = new(400, 300);
        TooltipMetrics metrics = TooltipMetrics.Default;
        metrics.AnchorOffsetX = 6;
        Rect measured = Tooltip.ComputeBounds(font, "Eat Bread", "", font, font,
            Array.Empty<TooltipLine>(), Vector2.Zero, viewport, metrics,
            float.PositiveInfinity, 1f, TooltipAnchorMode.Offset);
        Span<Rect> boxes = stackalloc Rect[1];
        TooltipStackLayout.Place([new(measured.Width, measured.Height)], new Vector2(100, 100),
            viewport, boxes);

        Vector2 anchor = TooltipStackLayout.AnchorFor(boxes[0], metrics);
        Rect actual = Tooltip.ComputeBounds(font, "Eat Bread", "", font, font,
            Array.Empty<TooltipLine>(), anchor, viewport, metrics,
            float.PositiveInfinity, 1f, TooltipAnchorMode.Offset);

        Assert.Equal(boxes[0], actual);
    }
}
