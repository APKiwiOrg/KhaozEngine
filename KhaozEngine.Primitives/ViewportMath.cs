using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>
/// Uniform-scale helpers for fitting one rectangle inside another while preserving aspect ratio (replaces the
/// open-coded <c>MathF.Min(w/H, h/H)</c> formula that was duplicated across windowing/camera/scene code), plus
/// device-pixel snapping for DPI-aware UI: rounding a point-space coordinate / rect / length so it lands on a
/// whole device pixel, which is what makes point-space chrome (1px borders, glyph origins) crisp instead of
/// fractionally straddling two device rows.
/// </summary>
public static class ViewportMath
{
    /// <summary>Largest uniform scale that fits src entirely inside dst (letterbox). Aspect preserved.</summary>
    public static float Fit(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Min(dstWidth / srcWidth, dstHeight / srcHeight);

    /// <summary>Smallest uniform scale that covers dst entirely with src (crop). Aspect preserved.</summary>
    public static float Cover(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Max(dstWidth / srcWidth, dstHeight / srcHeight);

    /// <summary>
    /// Destination rect for drawing a <paramref name="srcWidth"/> x <paramref name="srcHeight"/> image so it
    /// COVERS <paramref name="viewport"/> at a uniform (aspect-preserving) scale, with the image's normalized
    /// anchor (<paramref name="anchorU"/>, <paramref name="anchorV"/>, each 0..1) landing on <paramref name="anchor"/>.
    /// This is the rect form of <see cref="Cover(float,float,float,float)"/>: <c>Cover</c> gives the scale for a
    /// centred image, this gives the full rect for an image pinned by an arbitrary anchor - e.g. a camera-tracked
    /// background whose focal point follows a pan/zoom. The image is enlarged as far as needed to reach every
    /// viewport edge even when the anchor is off-centre (so no gap ever shows behind it), never below
    /// <paramref name="minHeight"/> (the caller's desired resting size - pass 0 for a pure cover), then a
    /// <paramref name="margin"/> (&gt;= 1) of slack is applied against sub-pixel edge seams. The result is
    /// undistorted: width and height keep the source aspect ratio.
    /// </summary>
    /// <param name="srcWidth">Source image width. Only its ratio to <paramref name="srcHeight"/> matters.</param>
    /// <param name="srcHeight">Source image height. A non-positive value is treated as a 1:1 aspect.</param>
    /// <param name="viewport">The rectangle to cover, in the same space as <paramref name="anchor"/>.</param>
    /// <param name="anchor">Screen point the source anchor is pinned to.</param>
    /// <param name="anchorU">Normalized horizontal source anchor (0 = left edge, 0.5 = centre, 1 = right edge).</param>
    /// <param name="anchorV">Normalized vertical source anchor (0 = top edge, 0.5 = centre, 1 = bottom edge).</param>
    /// <param name="minHeight">Lower bound on the drawn height, so the image never shrinks below a desired scale.</param>
    /// <param name="margin">Coverage slack multiplier (&gt;= 1); 1 is exact, ~1.02 hides sub-pixel edge seams.</param>
    public static Rect CoverAnchored(float srcWidth, float srcHeight, Rect viewport, Vector2 anchor,
        float anchorU = 0.5f, float anchorV = 0.5f, float minHeight = 0f, float margin = 1f)
    {
        float aspect = srcHeight > 0f ? srcWidth / srcHeight : 1f;

        // Clamp the anchor off the 0/1 extremes so each side's "reach" is never zero (which would demand an
        // infinite size). An anchor sitting on an edge still covers: the opposite side then drives the size.
        float u = Math.Clamp(anchorU, 1e-3f, 1f - 1e-3f);
        float v = Math.Clamp(anchorV, 1e-3f, 1f - 1e-3f);

        float ax = anchor.X - viewport.X; // anchor relative to the viewport origin
        float ay = anchor.Y - viewport.Y;

        // Width the image must span so it reaches both the left and right viewport edges from the anchor, and the
        // height for top/bottom. Fold the height requirement into a width via the aspect so one uniform size
        // satisfies both axes; hold the caller's minimum; then apply the slack margin. Height derives from aspect.
        float reqW = MathF.Max(ax / u, (viewport.Width - ax) / (1f - u));
        float reqH = MathF.Max(ay / v, (viewport.Height - ay) / (1f - v));
        float width = MathF.Max(MathF.Max(reqW, reqH * aspect), MathF.Max(minHeight, 0f) * aspect) * MathF.Max(1f, margin);
        float height = width / aspect;

        return new Rect(anchor.X - u * width, anchor.Y - v * height, width, height);
    }

    /// <summary>
    /// Snap an authoring coordinate so its mapped device pixel (<c>coord * scale + offset</c>) is a whole number,
    /// returned back in authoring units. A non-positive <paramref name="scale"/> (a non-pixel space) is a no-op.
    /// </summary>
    public static float SnapToDevicePixel(float coord, float scale, float offset = 0f)
        => scale <= 0f ? coord : (MathF.Round(coord * scale + offset) - offset) / scale;

    /// <summary>
    /// Snap a rect's edges (independently, so both sides land on device pixels and the width stays a whole pixel
    /// count) into authoring units. A non-positive scale on either axis is a no-op for that whole rect.
    /// </summary>
    public static Rect SnapRectToDevice(Rect r, Vector2 scale, Vector2 offset)
    {
        if (scale.X <= 0f || scale.Y <= 0f) return r;
        float left = SnapToDevicePixel(r.X, scale.X, offset.X);
        float top = SnapToDevicePixel(r.Y, scale.Y, offset.Y);
        float right = SnapToDevicePixel(r.X + r.Width, scale.X, offset.X);
        float bottom = SnapToDevicePixel(r.Y + r.Height, scale.Y, offset.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Snap a length (a delta, so no offset - e.g. a border thickness) to a whole number of device pixels,
    /// returned in authoring units. <paramref name="minDevicePixels"/> floors the result (pass 1 so a hairline
    /// border never rounds away to nothing). A non-positive scale is a no-op.
    /// </summary>
    public static float SnapLengthToDevice(float length, float scale, float minDevicePixels = 0f)
    {
        if (scale <= 0f) return length;
        float dev = MathF.Round(length * scale);
        if (dev < minDevicePixels) dev = minDevicePixels;
        return dev / scale;
    }
}
