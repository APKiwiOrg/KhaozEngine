using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>Which corner of the viewport the <see cref="DiagnosticsOverlay"/> panel anchors to.</summary>
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Look, layout, and toggle binding for <see cref="DiagnosticsOverlay"/>, mirroring the shape of
/// <see cref="UpdateOverlayTheme"/>. Every colour is injected (no hard-coded colours in the widget);
/// <see cref="Default"/> reproduces a neutral dark debug-panel palette anchored top-left with an F1 toggle.
/// Colours are <see cref="Vector4"/> (RGBA 0..1); the <see cref="Color"/> literals convert implicitly.
/// </summary>
public class DiagnosticsOverlayTheme
{
    // Toggle binding
    public Key ToggleKey = Key.F1;
    /// <summary>Optional gamepad toggle; null = keyboard only (the default).</summary>
    public GamepadButton? TriggerButton = null;

    // Anchor
    public OverlayCorner Corner = OverlayCorner.TopLeft;
    /// <summary>Gap (draw units) between the panel and the viewport edges.</summary>
    public float Margin = 12f;

    // Colours
    public Vector4 PanelFill = Color.FromBytes(8, 10, 18, 215);
    public Vector4 BorderColor = Color.FromBytes(80, 160, 255, 170);
    public Vector4 TitleText = Color.FromBytes(120, 200, 255);
    public Vector4 LabelText = Color.FromBytes(165, 175, 195);
    public Vector4 ValueText = Color.FromBytes(235, 240, 250);

    // Layout
    /// <summary>Inner padding between the panel border and its content.</summary>
    public float Padding = 10f;
    /// <summary>Vertical gap between consecutive rows.</summary>
    public float RowSpacing = 2f;
    /// <summary>Extra vertical gap before each section title (after the first).</summary>
    public float SectionSpacing = 8f;
    /// <summary>Minimum horizontal gap between a row's label and its right-aligned value.</summary>
    public float ColumnGap = 28f;
    /// <summary>Text scale applied to row text.</summary>
    public float Scale = 0.5f;
    /// <summary>Text scale applied to section titles (relative to the font, like <see cref="Scale"/>).</summary>
    public float TitleScale = 0.55f;
    public float BorderThickness = 1f;
    /// <summary>Fade speed in alpha units/sec; &lt;= 0 disables the fade (instant show/hide).</summary>
    public float FadeSpeed = 8f;

    /// <summary>A fresh default theme (neutral dark palette, top-left, F1 toggle, keyboard only).</summary>
    public static DiagnosticsOverlayTheme Default => new();
}
