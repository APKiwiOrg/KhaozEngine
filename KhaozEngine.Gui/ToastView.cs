using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Corner-anchored view over a <see cref="ToastStack"/>: pure layout (where each active toast's
/// <see cref="Rect"/> falls, derived deterministically from <see cref="Bounds"/>, <see cref="Theme"/>,
/// <see cref="Measurer"/>, and <see cref="Stack"/>'s current <see cref="ToastStack.Active"/> list), tap-dismiss
/// input (<see cref="Update"/>), and themed drawing (<see cref="Draw"/>). Both <see cref="Update"/> and
/// <see cref="Draw"/> call <see cref="GetToastBounds"/>, so hit-testing and pixels always agree on where a
/// toast is. <see cref="Measurer"/> is an <see cref="ITextMeasurer"/> rather than a concrete font so layout
/// stays headless-testable without a GPU device or a real font.
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

    /// <summary>
    /// Tap-dismiss input: for each active toast (newest first), reserve its bounds via
    /// <see cref="Pointer.BlockRegion"/> so a tap never leaks through to whatever the host draws underneath,
    /// then hit-test with <see cref="Pointer.IsTapIn"/> (the press-origin invariant, so a drag that started
    /// elsewhere can't dismiss a toast it merely ends over). The first toast that registers a valid tap is
    /// removed via <see cref="ToastStack.Dismiss(Toast)"/> and processing stops for this call, since the
    /// remaining toasts' bounds shift once <see cref="Stack"/> loses that entry and reserving them now against
    /// a rect that is about to move would be stale. They get their own <see cref="Pointer.BlockRegion"/> call
    /// next frame. Works identically for a sticky toast, whose only dismissal path is a tap. Returns true when
    /// a toast was dismissed.
    /// </summary>
    public bool Update(Pointer pointer)
    {
        var active = Stack.Active;
        for (int i = 0; i < active.Count; i++)
        {
            Rect bounds = GetToastBounds(i);
            pointer.BlockRegion(bounds);
            if (pointer.IsTapIn(bounds))
            {
                Stack.Dismiss(active[i]);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Draw every active toast (a no-op when <see cref="ToastStack.Active"/> is empty): the themed background
    /// and border from <see cref="ToastTheme.GetPalette"/>, the word-wrapped and vertically centered message
    /// text, and, for a non-sticky toast, a countdown timer bar along the bottom edge that shrinks as
    /// <see cref="Toast.Remaining"/> counts down toward zero. Bounds come from <see cref="GetToastBounds"/>,
    /// the same layout <see cref="Update"/> hit-tests against. <paramref name="white"/> is a 1x1 white texture
    /// used for the fills (see <see cref="GuiDraw"/>). <paramref name="font"/> must back <see cref="Measurer"/>
    /// so the drawn wrap matches the measured wrap.
    /// </summary>
    public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
    {
        var active = Stack.Active;
        if (active.Count == 0) return;

        float textWidth = Theme.Width - 2f * Theme.PaddingX;

        for (int i = 0; i < active.Count; i++)
        {
            Toast toast = active[i];
            Rect bounds = GetToastBounds(i);
            ToastPalette pal = Theme.GetPalette(toast.Kind);

            GuiDraw.Fill(batch, white, bounds, pal.Background);
            GuiDraw.Border(batch, white, bounds, Theme.BorderThickness, pal.Border);

            string text = toast.Message.Resolve();
            float textHeight = TextLayout.MeasureWrappedHeight(Measurer, text, textWidth);
            float textY = bounds.Y + (bounds.Height - textHeight) * 0.5f;
            TextLayout.DrawWrapped(batch, font, text, new Vector2(bounds.X + Theme.PaddingX, textY),
                textWidth, TextAlign.Left, (Color)pal.Text);

            if (!toast.IsSticky)
            {
                float progress = Math.Clamp(toast.Remaining / toast.Duration, 0f, 1f);
                float barWidth = (bounds.Width - 2f * Theme.BorderThickness) * progress;
                if (barWidth < 1f) continue;

                var bar = new Rect(
                    bounds.X + Theme.BorderThickness,
                    bounds.Bottom - Theme.BorderThickness - Theme.TimerBarHeight,
                    barWidth,
                    Theme.TimerBarHeight);
                GuiDraw.Fill(batch, white, bar, pal.TimerBar);
            }
        }
    }
}
