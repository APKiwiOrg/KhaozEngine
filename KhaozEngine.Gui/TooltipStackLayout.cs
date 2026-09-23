using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>Places independently sized tooltips as one ordered group beside a pointer. The group flips
/// together at viewport edges so bubbles that fit the viewport keep their order after clamping.</summary>
public static class TooltipStackLayout
{
    /// <summary>Converts a placed box into the anchor for an offset-mode <see cref="Tooltip"/>.
    /// Pass the same metrics to the tooltip so its measured bounds match the placed box.</summary>
    public static Vector2 AnchorFor(in Rect box, in TooltipMetrics metrics) =>
        new(box.X - metrics.AnchorOffsetX, box.Y + box.Height + metrics.AnchorOffsetY);

    /// <summary>Fills <paramref name="boxes"/> with the bounds of each tooltip in display order and returns
    /// the group's bounds. Callers measure each <see cref="Tooltip"/> first, then
    /// pass its width and height here. The host should keep the total group within the viewport, since an
    /// individual <see cref="Tooltip"/> still clamps its own bounds when drawn.</summary>
    public static Rect Place(ReadOnlySpan<Vector2> sizes, Vector2 pointer, Vector2 viewport,
        Span<Rect> boxes, float gap = 4f, float offset = 12f, float margin = 4f)
    {
        if (boxes.Length < sizes.Length) throw new ArgumentException("Output has fewer boxes than sizes.", nameof(boxes));
        if (sizes.IsEmpty) return default;

        float width = 0f;
        float height = 0f;
        for (int i = 0; i < sizes.Length; i++)
        {
            width = MathF.Max(width, sizes[i].X);
            height += sizes[i].Y;
        }
        height += gap * (sizes.Length - 1);

        float x = pointer.X + offset;
        if (x + width + margin > viewport.X) x = pointer.X - offset - width;
        x = Math.Clamp(x, margin, MathF.Max(margin, viewport.X - width - margin));

        float y = pointer.Y + offset;
        if (y + height + margin > viewport.Y) y = pointer.Y - offset - height;
        y = Math.Clamp(y, margin, MathF.Max(margin, viewport.Y - height - margin));

        float nextY = y;
        for (int i = 0; i < sizes.Length; i++)
        {
            boxes[i] = new Rect(x, nextY, sizes[i].X, sizes[i].Y);
            nextY += sizes[i].Y + gap;
        }
        return new Rect(x, y, width, height);
    }
}
