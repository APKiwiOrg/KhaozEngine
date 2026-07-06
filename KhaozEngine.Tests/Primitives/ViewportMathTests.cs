using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class ViewportMathTests
{
    [Fact]
    public void Fit_WiderSource_LimitedByWidth()
        => Assert.Equal(0.5f, ViewportMath.Fit(200, 100, 100, 100));

    [Fact]
    public void Fit_TallerSource_LimitedByHeight()
        => Assert.Equal(0.5f, ViewportMath.Fit(100, 200, 100, 100));

    [Fact]
    public void Cover_UsesMaxRatio()
        => Assert.Equal(1f, ViewportMath.Cover(200, 100, 100, 100));

    // --- device-pixel snapping (DPI-aware UI) ---

    [Fact]
    public void SnapToDevicePixel_rounds_the_mapped_pixel_to_a_whole_number()
    {
        // At 1.5x, a coord already on a device pixel is unchanged; an off-grid one snaps to the nearest.
        Assert.Equal(10f, ViewportMath.SnapToDevicePixel(10f, 1.5f), 4);          // 15.0 device px, whole
        Assert.Equal(16f / 1.5f, ViewportMath.SnapToDevicePixel(10.4f, 1.5f), 4); // 15.6 -> 16 device px
    }

    [Fact]
    public void SnapToDevicePixel_is_identity_when_scale_is_non_positive()
        => Assert.Equal(10.4f, ViewportMath.SnapToDevicePixel(10.4f, 0f), 5);

    [Fact]
    public void SnapRectToDevice_lands_both_edges_on_device_pixels()
    {
        var scale = new Vector2(1.5f, 1.5f);
        Rect s = ViewportMath.SnapRectToDevice(new Rect(10.4f, 20.6f, 100.3f, 50.1f), scale, Vector2.Zero);

        // Every edge, mapped to device pixels, is a whole number - the fractional phase that made borders uneven is gone.
        Assert.Equal(MathF.Round(s.X * 1.5f), s.X * 1.5f, 3);
        Assert.Equal(MathF.Round(s.Y * 1.5f), s.Y * 1.5f, 3);
        Assert.Equal(MathF.Round((s.X + s.Width) * 1.5f), (s.X + s.Width) * 1.5f, 3);
        Assert.Equal(MathF.Round((s.Y + s.Height) * 1.5f), (s.Y + s.Height) * 1.5f, 3);
    }

    [Fact]
    public void SnapRectToDevice_is_identity_when_not_snappable()
    {
        var r = new Rect(10.4f, 20.6f, 100.3f, 50.1f);
        Assert.Equal(r, ViewportMath.SnapRectToDevice(r, Vector2.Zero, Vector2.Zero));
    }

    [Fact]
    public void SnapLengthToDevice_rounds_thickness_to_whole_device_pixels()
    {
        Assert.Equal(2f / 1.5f, ViewportMath.SnapLengthToDevice(1f, 1.5f, minDevicePixels: 1f), 4); // 1.5 -> 2 device px
        Assert.Equal(0.5f, ViewportMath.SnapLengthToDevice(0.1f, 2f, minDevicePixels: 1f), 4);       // 0.2 -> floored to 1 device px
        Assert.Equal(0.1f, ViewportMath.SnapLengthToDevice(0.1f, 0f, minDevicePixels: 1f), 5);       // non-snappable -> unchanged
    }
}
