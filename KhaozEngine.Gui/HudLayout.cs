using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>A HUD region's center as fractions of the UI canvas, suitable for local settings storage.</summary>
public readonly record struct HudLayoutPosition(float CenterX, float CenterY);

/// <summary>A stable region identifier and its current default rectangle. List regions in draw order.</summary>
public readonly record struct HudLayoutRegion(string Id, Rect DefaultBounds);

/// <summary>Resolves caller-owned HUD geometry without changing saved normalized positions.</summary>
public static class HudLayout
{
    /// <summary>
    /// Apply a saved center, or the caller's default if no valid center exists, then clamp the rectangle to
    /// the canvas. An oversized region retains its size with its top-left reachable. An unsized canvas is
    /// treated as zero extent so callers can lay out before their first window frame.
    /// </summary>
    public static Rect Resolve(in Rect defaultBounds, Vector2 viewport, HudLayoutPosition? position)
    {
        ValidateBounds(defaultBounds);
        float width = Extent(viewport.X);
        float height = Extent(viewport.Y);
        float x = defaultBounds.X;
        float y = defaultBounds.Y;
        if (position is { } saved && IsValid(saved))
        {
            x = saved.CenterX * width - defaultBounds.Width * 0.5f;
            y = saved.CenterY * height - defaultBounds.Height * 0.5f;
        }
        return new Rect(
            Math.Clamp(x, 0f, MathF.Max(0f, width - defaultBounds.Width)),
            Math.Clamp(y, 0f, MathF.Max(0f, height - defaultBounds.Height)),
            defaultBounds.Width, defaultBounds.Height);
    }

    /// <summary>Capture a rectangle's normalized center on a positive, finite canvas.</summary>
    public static HudLayoutPosition FromBounds(in Rect bounds, Vector2 viewport)
    {
        ValidateBounds(bounds);
        if (!HasExtent(viewport))
            throw new ArgumentOutOfRangeException(nameof(viewport), "A saved HUD position needs a sized canvas.");
        return new HudLayoutPosition(
            (float)Math.Clamp(((double)bounds.X + bounds.Width * 0.5d) / viewport.X, 0d, 1d),
            (float)Math.Clamp(((double)bounds.Y + bounds.Height * 0.5d) / viewport.Y, 0d, 1d));
    }

    internal static bool IsValid(HudLayoutPosition position) =>
        float.IsFinite(position.CenterX) && position.CenterX >= 0f && position.CenterX <= 1f
        && float.IsFinite(position.CenterY) && position.CenterY >= 0f && position.CenterY <= 1f;

    internal static bool HasExtent(Vector2 viewport) =>
        float.IsFinite(viewport.X) && viewport.X > 0f && float.IsFinite(viewport.Y) && viewport.Y > 0f;

    static float Extent(float value) => float.IsFinite(value) && value > 0f ? value : 0f;

    static void ValidateBounds(in Rect bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
            || bounds.Width < 0f || bounds.Height < 0f)
            throw new ArgumentOutOfRangeException(nameof(bounds), "HUD bounds must be finite with nonnegative size.");
    }
}
