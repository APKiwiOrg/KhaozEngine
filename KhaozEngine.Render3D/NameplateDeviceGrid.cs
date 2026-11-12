using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Places one plate's geometry on the active pass's device-pixel grid as a rigid block.</summary>
/// <remarks>
/// A plate's anchor is a projected world point, so it lands on a fractional device pixel and drifts through
/// sub-pixel phases as the camera or the entity moves. An anti-aliased edge drawn there changes coverage with the
/// phase, which shows as a thin border pulsing or dropping out in motion while the snapped text beside it stays
/// crisp. The panel origin snaps to a device pixel, and every component is then placed from its offset to that
/// origin, rounded once in device pixels, with its size snapped as a length. Rounding the absolute coordinate
/// instead makes the result depend on the origin: a half pixel offset rounds up on one row and down on the next,
/// so a bar changed height as its plate moved. Outside a point-space UI pass the scale is zero and every member
/// returns the unsnapped geometry, so framebuffer, design-space and world output is unchanged.
/// </remarks>
internal readonly struct NameplateDeviceGrid
{
    readonly Vector2 _scale;
    readonly Vector2 _origin;

    NameplateDeviceGrid(Vector2 scale, Vector2 origin)
    {
        _scale = scale;
        _origin = origin;
    }

    /// <summary>The grid for a panel whose unsnapped origin is <paramref name="panelRect"/>'s.</summary>
    public static NameplateDeviceGrid At<TCanvas>(ref TCanvas canvas, Vector4 panelRect)
        where TCanvas : INameplateCanvas
    {
        Vector2 scale = canvas.DeviceScale;
        if (scale.X <= 0f || scale.Y <= 0f) return default;
        Vector2 offset = canvas.DeviceOffset;
        return new NameplateDeviceGrid(scale, new Vector2(
            ViewportMath.SnapToDevicePixel(panelRect.X, scale.X, offset.X),
            ViewportMath.SnapToDevicePixel(panelRect.Y, scale.Y, offset.Y)));
    }

    bool Snaps => _scale.X > 0f;

    /// <summary>The panel at its snapped origin, with its size a whole number of device pixels.</summary>
    public Vector4 Panel(Vector4 panelRect) => Snaps
        ? new Vector4(_origin.X, _origin.Y, Size(panelRect.Z, _scale.X), Size(panelRect.W, _scale.Y))
        : panelRect;

    /// <summary>A component rect, from its layout rect and the layout-to-panel offset.</summary>
    /// <param name="layout">The component's resolved rect in layout space.</param>
    /// <param name="local">Layout space to the panel origin, which is the same on every frame.</param>
    /// <param name="screen">Layout space to the unsnapped panel, for a pass that does not snap.</param>
    public Vector4 Place(Rect layout, Vector2 local, Vector2 screen) => Snaps
        ? new Vector4(
            _origin.X + Offset(layout.X + local.X, _scale.X),
            _origin.Y + Offset(layout.Y + local.Y, _scale.Y),
            Size(layout.Width, _scale.X),
            Size(layout.Height, _scale.Y))
        : new Vector4(layout.X + screen.X, layout.Y + screen.Y, layout.Width, layout.Height);

    /// <summary>A border thickness as a whole number of device pixels, never less than one. Zero stays zero,
    /// which draws no border.</summary>
    public float SnapThickness(float thickness) => Snaps && thickness > 0f
        ? ViewportMath.SnapLengthToDevice(thickness, _scale.X, minDevicePixels: 1f)
        : thickness;

    static float Offset(float points, float scale) => MathF.Round(points * scale) / scale;

    // A drawn size never rounds away to nothing. A zero size stays zero.
    static float Size(float points, float scale) => points > 0f
        ? ViewportMath.SnapLengthToDevice(points, scale, minDevicePixels: 1f)
        : points;
}
