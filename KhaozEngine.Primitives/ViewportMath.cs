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
