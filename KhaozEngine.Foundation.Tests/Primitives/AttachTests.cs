using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class AttachTests
{
    [Theory]
    [InlineData(AttachPoint.TopLeft, 10f, 20f)]
    [InlineData(AttachPoint.Top, 60f, 20f)]
    [InlineData(AttachPoint.TopRight, 110f, 20f)]
    [InlineData(AttachPoint.Left, 10f, 45f)]
    [InlineData(AttachPoint.Center, 60f, 45f)]
    [InlineData(AttachPoint.Right, 110f, 45f)]
    [InlineData(AttachPoint.BottomLeft, 10f, 70f)]
    [InlineData(AttachPoint.Bottom, 60f, 70f)]
    [InlineData(AttachPoint.BottomRight, 110f, 70f)]
    public void PointOf_namesEveryPointOfTheRect(AttachPoint point, float x, float y) =>
        Assert.Equal(new Vector2(x, y), Attach.PointOf(new Rect(10f, 20f, 100f, 50f), point));

    [Fact]
    public void Resolve_hangsAHeaderAboveABar() =>
        Assert.Equal(new Rect(0f, -16f, 40f, 14f),
            Attach.Resolve(new Rect(0f, 0f, 112f, 9f), AttachPoint.TopLeft, 40f, 14f, AttachPoint.BottomLeft, 0f, -2f));

    [Fact]
    public void Resolve_pinsABadgeToTheBarsTopRight() =>
        Assert.Equal(new Rect(92f, -15f, 20f, 13f),
            Attach.Resolve(new Rect(0f, 0f, 112f, 9f), AttachPoint.TopRight, 20f, 13f, AttachPoint.BottomRight, 0f, -2f));

    [Fact]
    public void Resolve_ofZeroSizeIsThePoint() =>
        Assert.Equal(new Rect(56f, 4.5f, 0f, 0f),
            Attach.Resolve(new Rect(0f, 0f, 112f, 9f), AttachPoint.Center, 0f, 0f, AttachPoint.Center));

    [Fact]
    public void Resolve_alignsDifferentPointsWithBothOffsetsOnATranslatedTarget() =>
        Assert.Equal(new Rect(23f, 43f, 40f, 14f),
            Attach.Resolve(new Rect(10f, 20f, 100f, 50f), AttachPoint.Center, 40f, 14f, AttachPoint.TopRight, 3f, -2f));

    [Fact]
    public void Union_coversBoth() =>
        Assert.Equal(new Rect(0f, -5f, 15f, 15f), Attach.Union(new Rect(0f, 0f, 10f, 10f), new Rect(5f, -5f, 10f, 10f)));
}
