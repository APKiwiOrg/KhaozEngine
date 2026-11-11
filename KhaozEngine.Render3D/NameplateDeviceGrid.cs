using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>The active pass's device-pixel grid, which plate geometry snaps to.</summary>
/// <remarks>
/// A plate's anchor is a projected world point, so it lands on a fractional device pixel and drifts through
/// sub-pixel phases as the camera or the entity moves. An anti-aliased edge drawn there changes coverage with the
/// phase, which shows as a thin border pulsing or dropping out in motion while the snapped text beside it stays
/// crisp. Snapping the panel origin keeps the components moving as one block, and snapping each rect and border
/// thickness keeps their edges on whole device pixels. Outside a point-space UI pass the scale is zero and every
/// member returns its input, so framebuffer, design-space and world output is unchanged.
/// </remarks>
internal readonly struct NameplateDeviceGrid(Vector2 scale, Vector2 offset)
{
    public static NameplateDeviceGrid Of<TCanvas>(ref TCanvas canvas)
        where TCanvas : INameplateCanvas => new(canvas.DeviceScale, canvas.DeviceOffset);

    bool Snaps => scale.X > 0f && scale.Y > 0f;

    /// <summary>The panel rect moved so its origin sits on a whole device pixel, with its size kept.</summary>
    public Vector4 AtSnappedOrigin(Vector4 rect) => Snaps
        ? rect with
        {
            X = ViewportMath.SnapToDevicePixel(rect.X, scale.X, offset.X),
            Y = ViewportMath.SnapToDevicePixel(rect.Y, scale.Y, offset.Y),
        }
        : rect;

    /// <summary>The rect with every edge on a whole device pixel.</summary>
    public Vector4 SnapRect(Vector4 rect)
    {
        if (!Snaps) return rect;
        Rect snapped = ViewportMath.SnapRectToDevice(new Rect(rect.X, rect.Y, rect.Z, rect.W), scale, offset);
        return new Vector4(snapped.X, snapped.Y, snapped.Width, snapped.Height);
    }

    /// <summary>A border thickness as a whole number of device pixels, never less than one. Zero stays zero,
    /// which draws no border.</summary>
    public float SnapThickness(float thickness) => Snaps && thickness > 0f
        ? ViewportMath.SnapLengthToDevice(thickness, scale.X, minDevicePixels: 1f)
        : thickness;
}
