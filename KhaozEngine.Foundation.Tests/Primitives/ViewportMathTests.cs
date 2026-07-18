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

    // --- anchored cover rect ---

    // Helper: does dst cover the whole viewport (reaches every edge)?
    private static bool Covers(Rect dst, Rect vp)
        => dst.X <= vp.X + 1e-3f && dst.Y <= vp.Y + 1e-3f
        && dst.Right >= vp.Right - 1e-3f && dst.Bottom >= vp.Bottom - 1e-3f;

    [Fact]
    public void CoverAnchored_centred_square_covers_and_keeps_aspect()
    {
        var vp = new Rect(0, 0, 100, 200); // portrait viewport, square source
        Rect d = ViewportMath.CoverAnchored(50, 50, vp, new Vector2(50, 100));
        Assert.True(Covers(d, vp));
        Assert.Equal(d.Width, d.Height, 3);        // square source stays square (aspect preserved)
        Assert.Equal(200f, d.Height, 3);           // cover a 100x200 viewport with a 1:1 image -> 200x200
        Assert.Equal(50f, d.X + d.Width / 2f, 3);  // centred on the anchor
        Assert.Equal(100f, d.Y + d.Height / 2f, 3);
    }

    [Fact]
    public void CoverAnchored_portrait_source_in_landscape_viewport_covers_without_stretch()
    {
        // The Nullwake failure mode: a 2:3 portrait image over a 16:9 window must cover, not stretch.
        var vp = new Rect(0, 0, 1000, 820);
        Rect d = ViewportMath.CoverAnchored(960, 1440, vp, new Vector2(500, 410));
        Assert.True(Covers(d, vp));
        Assert.Equal(960f / 1440f, d.Width / d.Height, 3); // undistorted: keeps the source aspect
    }

    [Fact]
    public void CoverAnchored_offcentre_anchor_still_covers_every_edge()
    {
        // A hard-panned anchor near a corner (the grey-gap case): the far sides must still be reached.
        var vp = new Rect(0, 0, 400, 900);
        Rect d = ViewportMath.CoverAnchored(400, 600, vp, new Vector2(360, 800));
        Assert.True(Covers(d, vp));
    }

    [Fact]
    public void CoverAnchored_anchor_outside_viewport_still_covers()
    {
        // Extreme pan can push the focal point off-screen entirely; coverage must not break.
        var vp = new Rect(0, 0, 400, 900);
        Rect d = ViewportMath.CoverAnchored(400, 600, vp, new Vector2(-120, 950));
        Assert.True(Covers(d, vp));
    }

    [Fact]
    public void CoverAnchored_minHeight_sets_a_lower_bound_on_scale()
    {
        // When the pure-cover size is small, minHeight holds the image at the desired resting scale.
        var vp = new Rect(0, 0, 100, 100);
        Rect small = ViewportMath.CoverAnchored(100, 100, vp, new Vector2(50, 50));
        Rect big = ViewportMath.CoverAnchored(100, 100, vp, new Vector2(50, 50), minHeight: 400f);
        Assert.Equal(100f, small.Height, 3);       // cover alone needs only 100
        Assert.Equal(400f, big.Height, 3);         // minHeight floors it to 400
        Assert.True(Covers(big, vp));
    }

    [Fact]
    public void CoverAnchored_margin_enlarges_past_exact_cover()
    {
        var vp = new Rect(0, 0, 100, 200);
        Rect exact = ViewportMath.CoverAnchored(50, 50, vp, new Vector2(50, 100));
        Rect slack = ViewportMath.CoverAnchored(50, 50, vp, new Vector2(50, 100), margin: 1.1f);
        Assert.Equal(exact.Height * 1.1f, slack.Height, 3);
    }

    [Fact]
    public void CoverAnchored_nonpositive_source_height_treated_as_square()
    {
        var vp = new Rect(0, 0, 100, 100);
        Rect d = ViewportMath.CoverAnchored(100, 0, vp, new Vector2(50, 50));
        Assert.Equal(d.Width, d.Height, 3); // 1:1 fallback, no divide-by-zero
        Assert.True(Covers(d, vp));
    }

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
