using System;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

public sealed partial class ScrollablePanel
{
    /// <summary>Opt-in scrolling while a <see cref="GuiDragContext"/> drag is held in the top or bottom edge
    /// band of <see cref="ContentBounds"/>. False by default.</summary>
    public bool DragEdgeScrollingEnabled;

    /// <summary>Depth of each drag-scroll edge band in design pixels. The effective value is clamped to half
    /// the content height so the top and bottom bands never overlap. Defaults to 40.</summary>
    public float DragEdgeScrollBand = 40f;

    /// <summary>Maximum drag-edge scroll rate in design pixels per second, reached at the content edge.
    /// Defaults to 360.</summary>
    public float DragEdgeScrollSpeed = 360f;

    /// <summary>Runs the normal panel update, then applies opt-in drag-edge scrolling from the pointer position
    /// sampled by <paramref name="dragContext"/>. The rate grows linearly from zero at the inner edge of a band
    /// to <see cref="DragEdgeScrollSpeed"/> at the content edge. The header is excluded because the bands are
    /// derived from <see cref="ContentBounds"/>.</summary>
    public void Update(Pointer pointer, InputState input, float dt, GuiDragContext dragContext)
    {
        ArgumentNullException.ThrowIfNull(dragContext);
        bool dragPanEnabled = DragScrollingEnabled;
        if (dragContext.IsDragging)
            DragScrollingEnabled = false;
        try
        {
            Update(pointer, input, dt);
        }
        finally
        {
            DragScrollingEnabled = dragPanEnabled;
        }
        ApplyDragEdgeScroll(dragContext, dt);
    }

    void ApplyDragEdgeScroll(GuiDragContext dragContext, float dt)
    {
        if (!DragEdgeScrollingEnabled || !dragContext.IsDragging || dt <= 0f || MaxScroll <= 0f ||
            TransitionAlpha <= 0f)
            return;

        Rect content = ContentBounds;
        float band = Math.Clamp(DragEdgeScrollBand, 0f, content.Height * 0.5f);
        float speed = MathF.Max(0f, DragEdgeScrollSpeed);
        if (band <= 0f || speed <= 0f)
            return;

        var position = dragContext.PointerPosition;
        if (position.X < content.X || position.X > content.Right ||
            position.Y < content.Y || position.Y > content.Bottom)
            return;

        float delta = 0f;
        if (position.Y < content.Y + band)
            delta = -(content.Y + band - position.Y) / band;
        else if (position.Y > content.Bottom - band)
            delta = (position.Y - (content.Bottom - band)) / band;

        if (delta != 0f)
            ScrollTo(ScrollOffset + Math.Clamp(delta, -1f, 1f) * speed * dt);
    }
}
