using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class UiViewportScaleTests
{
    [Theory]
    [InlineData(1280, 720, 0.5f, 2560, 1440, 40f, 20f)]
    [InlineData(1280, 720, 1f, 1280, 720, 80f, 40f)]
    [InlineData(1280, 720, 0.1f, 12800, 7200, 8f, 4f)]
    [InlineData(1280, 720, 10f, 128, 72, 800f, 400f)]
    [InlineData(1920, 1080, 2f, 640, 360, 240f, 120f)]
    [InlineData(2560, 1440, 2f, 640, 360, 320f, 160f)]
    public void UserScaleChangesTheCanvasAndPointerInTheSameSpace(int pixelsWide, int pixelsHigh,
        float multiplier, int logicalWide, int logicalHigh, float deviceX, float deviceY)
    {
        var viewport = new UiViewport(pixelsWide, pixelsHigh, 1280, 720);
        RequestScale(viewport, multiplier);
        viewport.Update(pixelsWide, pixelsHigh, 1280, 720);

        Assert.Equal(logicalWide, viewport.Width);
        Assert.Equal(logicalHigh, viewport.Height);
        Assert.Equal(new Vector2(deviceX, deviceY), viewport.DesignToScreen(new Vector2(80f, 40f)));
        Assert.Equal(new Vector2(80f, 40f), viewport.ScreenToDesign(new Vector2(deviceX, deviceY)));
        Assert.Equal(pixelsWide / 1280f, viewport.DpiScale);
        Assert.Equal(new Rect(0, 0, pixelsWide, pixelsHigh), viewport.ContentBounds);
    }

    [Fact]
    public void ScaleRequestDoesNotChangeAnAlreadyMappedFrame()
    {
        var viewport = new UiViewport(2560, 1440, 1280, 720);
        Matrix4x4 before = viewport.GetClipProjection(2560, 1440);
        RequestScale(viewport, 2f);

        Assert.Equal(1280, viewport.Width);
        Assert.Equal(new Vector2(100f, 50f), viewport.ScreenToDesign(new Vector2(200f, 100f)));
        Assert.Equal(before, viewport.GetClipProjection(2560, 1440));

        viewport.Update(new Frame { Width = 2560, Height = 1440, LogicalWidth = 1280, LogicalHeight = 720 });

        Assert.Equal(640, viewport.Width);
        Assert.Equal(new Vector2(50f, 25f), viewport.ScreenToDesign(new Vector2(200f, 100f)));
        Assert.NotEqual(before, viewport.GetClipProjection(2560, 1440));
    }

    [Fact]
    public void FramebufferOnlyUpdatesKeepPhysicalDpiAndApplyUserScaleOnce()
    {
        var viewport = new UiViewport(2560, 1440, 1280, 720);
        RequestScale(viewport, 2f);

        ((IDesignViewport)viewport).Update(3200, 1800);

        Assert.Equal(800, viewport.Width);
        Assert.Equal(450, viewport.Height);
        Assert.Equal(2f, viewport.DpiScale);
        Assert.Equal(4f, viewport.ScaleX);
        Assert.Equal(new Vector2(400f, 200f), viewport.DesignToScreen(new Vector2(100f, 50f)));
    }

    [Fact]
    public void FractionalLogicalRoundingDoesNotInventFramebufferMargins()
    {
        var viewport = new UiViewport(1537, 961, 1025, 641);
        RequestScale(viewport, 1.33f);
        viewport.Update(1537, 961, 1025, 641);

        Assert.Equal(new Rect(0, 0, 1537, 961), viewport.ContentBounds);
        Vector4 corner = Vector4.Transform(new Vector4(viewport.DesignBounds.Right,
            viewport.DesignBounds.Bottom, 0, 1), viewport.GetClipProjection(1537, 961));
        Assert.Equal(1f, corner.X, 5);
        Assert.Equal(-1f, corner.Y, 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(0.01f)]
    [InlineData(11f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidScaleRequestsDoNotReplaceTheLastGoodRequest(float invalid)
    {
        var viewport = new UiViewport(2560, 1440, 1280, 720) { ScaleMultiplier = 2f };

        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.ScaleMultiplier = invalid);
        viewport.Update(2560, 1440, 1280, 720);

        Assert.Equal(2f, viewport.ScaleMultiplier);
        Assert.Equal(4f, viewport.EffectiveScale);
        Assert.Equal(640, viewport.Width);
    }

    static void RequestScale(UiViewport viewport, float multiplier) => viewport.ScaleMultiplier = multiplier;
}
