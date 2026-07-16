using System;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>
/// Corner-anchored view over a <see cref="ToastStack"/>. This first slice covers only the pure layout: where
/// each active toast's <see cref="Rect"/> falls, derived deterministically from <see cref="Bounds"/>,
/// <see cref="Theme"/>, <see cref="Measurer"/>, and <see cref="Stack"/>'s current <see cref="ToastStack.Active"/>
/// list, so both input hit-testing and drawing (added by a later slice) call <see cref="GetToastBounds"/> and
/// agree on where a toast is. <see cref="Measurer"/> is an <see cref="ITextMeasurer"/> rather than a concrete
/// font so layout stays headless-testable without a GPU device or a real font.
/// </summary>
public sealed class ToastView
{
    /// <summary>Design-space region the stack anchors within (the host sets this, e.g. the viewport design bounds).</summary>
    public Rect Bounds;

    /// <summary>Look and layout knobs. Never null.</summary>
    public ToastTheme Theme;

    /// <summary>
    /// The text measurer used to size toasts. Must be the same font later passed to the draw pass so measured
    /// layout matches rendered layout.
    /// </summary>
    public ITextMeasurer Measurer;

    /// <summary>The retained toast model this view renders and lays out.</summary>
    public ToastStack Stack { get; }

    /// <summary>Wraps <paramref name="stack"/>, measuring text with <paramref name="measurer"/> and laying out with <paramref name="theme"/> (or <see cref="ToastTheme.Default"/>).</summary>
    public ToastView(ToastStack stack, ITextMeasurer measurer, ToastTheme? theme = null)
    {
        Stack = stack;
        Measurer = measurer;
        Theme = theme ?? ToastTheme.Default;
    }

    /// <summary>
    /// The design-space bounds of <c>Stack.Active[index]</c>: a fixed <see cref="ToastTheme.Width"/> and a
    /// height that grows past <see cref="ToastTheme.MinHeight"/> to fit the wrapped message, anchored to
    /// <see cref="ToastTheme.Corner"/> of <see cref="Bounds"/> and stacked toward the interior so index 0
    /// (newest) sits nearest the anchored corner.
    /// </summary>
    public Rect GetToastBounds(int index)
    {
        var active = Stack.Active;
        float textWidth = Theme.Width - 2f * Theme.PaddingX;

        bool right = Theme.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool top = Theme.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;

        float x = right
            ? Bounds.Right - Theme.Width - Theme.MarginX
            : Bounds.X + Theme.MarginX;

        float height = MeasureToastHeight(active[index], textWidth);

        float y;
        if (top)
        {
            y = Bounds.Y + Theme.MarginY;
            for (int i = 0; i < index; i++)
                y += MeasureToastHeight(active[i], textWidth) + Theme.Gap;
        }
        else
        {
            y = Bounds.Bottom - Theme.MarginY - height;
            for (int i = 0; i < index; i++)
                y -= MeasureToastHeight(active[i], textWidth) + Theme.Gap;
        }

        return new Rect(x, y, Theme.Width, height);
    }

    float MeasureToastHeight(Toast toast, float textWidth)
    {
        float wrapped = TextLayout.MeasureWrappedHeight(Measurer, toast.Message.Resolve(), textWidth);
        return MathF.Max(Theme.MinHeight, 2f * Theme.PaddingY + MathF.Ceiling(wrapped));
    }
}
