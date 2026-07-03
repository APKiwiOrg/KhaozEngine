using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>
/// Look and layout for <see cref="OverlayLegend"/>. Every colour is injected (no hard-coded colours in the
/// widget); <see cref="Default"/> reproduces the neutral grey debug-panel palette the legend shipped with.
/// <see cref="FromDiagnostics"/> derives a matching palette from a <see cref="DiagnosticsOverlayTheme"/> so a
/// legend drawn next to a <see cref="DiagnosticsOverlay"/> shares its panel/border/text look and text scale.
/// Colours are <see cref="Vector4"/> (RGBA 0..1); the <see cref="Color"/> literals convert implicitly.
/// </summary>
public class OverlayLegendTheme
{
    // Anchor (used by the Draw(..., Rect viewport) overload; the explicit-position overload ignores it).
    /// <summary>Which viewport corner the panel anchors to when drawn against a viewport rect.</summary>
    public OverlayCorner Corner = OverlayCorner.TopLeft;
    /// <summary>Gap (draw units) between the panel and the viewport edges when corner-anchored.</summary>
    public float Margin = 12f;

    // Colours
    public Vector4 PanelFill = new(0.05f, 0.06f, 0.09f, 0.75f);
    public Vector4 BorderColor = new(0.25f, 0.28f, 0.34f, 0.9f);
    public Vector4 LabelText = new(0.92f, 0.94f, 0.97f, 1f);
    public float BorderThickness = 1f;

    // Layout
    /// <summary>Inner padding between the panel border and its content.</summary>
    public float Padding = 8f;
    /// <summary>Size (square, draw units) of each row's colour swatch.</summary>
    public float SwatchSize = 14f;
    /// <summary>Horizontal gap between a row's swatch and its label.</summary>
    public float SwatchGap = 8f;
    /// <summary>Vertical gap between consecutive rows.</summary>
    public float RowSpacing = 4f;
    /// <summary>Scale applied to label text (1 = native font size).</summary>
    public float TextScale = 1f;

    /// <summary>A fresh default theme (the neutral grey legend palette, top-left, native-size labels).</summary>
    public static OverlayLegendTheme Default => new();

    /// <summary>
    /// A legend theme whose panel fill, border, label colour, border thickness, padding, corner/margin, and text
    /// scale are copied from <paramref name="diag"/>, so a legend drawn beside a <see cref="DiagnosticsOverlay"/>
    /// shares its look. Legend-specific layout (<see cref="SwatchSize"/>, <see cref="SwatchGap"/>,
    /// <see cref="RowSpacing"/>) keeps its own defaults. The label colour tracks the diagnostics value text
    /// (its near-white row text), not the dimmer label text.
    /// </summary>
    public static OverlayLegendTheme FromDiagnostics(DiagnosticsOverlayTheme diag) => new()
    {
        Corner = diag.Corner,
        Margin = diag.Margin,
        PanelFill = diag.PanelFill,
        BorderColor = diag.BorderColor,
        LabelText = diag.ValueText,
        BorderThickness = diag.BorderThickness,
        Padding = diag.Padding,
        TextScale = diag.Scale,
    };

    /// <summary>The panel's top-left position when anchored to <see cref="Corner"/> inside
    /// <paramref name="viewport"/>, given a measured panel size. Font-free, so it is headless-testable.</summary>
    public Vector2 Anchor(Rect viewport, float panelW, float panelH)
    {
        float left = viewport.X + Margin;
        float top = viewport.Y + Margin;
        float rightX = viewport.Right - Margin - panelW;
        float bottomY = viewport.Bottom - Margin - panelH;
        return Corner switch
        {
            OverlayCorner.TopRight => new Vector2(rightX, top),
            OverlayCorner.BottomLeft => new Vector2(left, bottomY),
            OverlayCorner.BottomRight => new Vector2(rightX, bottomY),
            _ => new Vector2(left, top), // TopLeft
        };
    }
}
