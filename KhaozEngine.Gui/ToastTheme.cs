using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>Background, border, timer-bar, and text colours a <see cref="ToastView"/> uses for one <see cref="ToastKind"/>.</summary>
public readonly record struct ToastPalette(Vector4 Background, Vector4 Border, Vector4 TimerBar, Vector4 Text);

/// <summary>
/// Look and layout for <see cref="ToastView"/>, mirroring the shape of <see cref="DiagnosticsOverlayTheme"/>.
/// Every colour is injected via a <see cref="ToastPalette"/> per <see cref="ToastKind"/> (no hard-coded colours
/// in the widget). <see cref="Default"/> reproduces a neutral dark palette anchored to the top-right corner.
/// Colours are <see cref="Vector4"/> (RGBA 0..1). The <see cref="Color"/> literals convert implicitly.
/// </summary>
public class ToastTheme
{
    // Anchor
    /// <summary>Which corner of the host's bounds the toast stack anchors to.</summary>
    public OverlayCorner Corner = OverlayCorner.TopRight;

    // Layout
    /// <summary>Fixed width (draw units) of every toast.</summary>
    public float Width = 200f;
    /// <summary>Minimum height (draw units) of a toast before wrapped message text grows it.</summary>
    public float MinHeight = 36f;
    /// <summary>Vertical gap (draw units) between consecutive stacked toasts.</summary>
    public float Gap = 4f;
    /// <summary>Horizontal inner padding between a toast's border and its message text.</summary>
    public float PaddingX = 8f;
    /// <summary>Vertical inner padding between a toast's border and its message text.</summary>
    public float PaddingY = 6f;
    /// <summary>Height (draw units) of the countdown timer bar drawn along a non-sticky toast's edge.</summary>
    public float TimerBarHeight = 2f;
    /// <summary>Thickness (draw units) of a toast's border stroke.</summary>
    public float BorderThickness = 1f;
    /// <summary>Gap (draw units) between the stack and the host bounds' horizontal edge.</summary>
    public float MarginX = 6f;
    /// <summary>Gap (draw units) between the stack and the host bounds' vertical edge.</summary>
    public float MarginY = 6f;

    // Colours
    /// <summary>Palette used for <see cref="ToastKind.Standard"/> toasts.</summary>
    public ToastPalette Standard = new(
        Color.FromBytes(15, 25, 50, 220),
        Color.FromBytes(60, 120, 200),
        Color.FromBytes(100, 180, 255),
        Color.White);

    /// <summary>Palette used for <see cref="ToastKind.Warning"/> toasts.</summary>
    public ToastPalette Warning = new(
        Color.FromBytes(50, 40, 10, 220),
        Color.FromBytes(200, 160, 50),
        Color.FromBytes(255, 200, 60),
        Color.White);

    /// <summary>Palette used for <see cref="ToastKind.Danger"/> toasts.</summary>
    public ToastPalette Danger = new(
        Color.FromBytes(60, 15, 15, 220),
        Color.FromBytes(200, 60, 60),
        Color.FromBytes(255, 80, 80),
        Color.White);

    /// <summary>The palette to use for a toast of this <paramref name="kind"/>.</summary>
    public ToastPalette GetPalette(ToastKind kind) => kind switch
    {
        ToastKind.Warning => Warning,
        ToastKind.Danger => Danger,
        _ => Standard,
    };

    /// <summary>A fresh default theme (neutral dark palette, top-right anchor).</summary>
    public static ToastTheme Default => new();
}
