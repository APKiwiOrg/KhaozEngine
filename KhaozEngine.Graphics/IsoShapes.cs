using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Pure geometry for the isometric primitives drawn by <see cref="PrimitiveRenderer"/>. Extracted
/// so the corner/face math is unit-testable without a <c>GraphicsDevice</c> (the draw methods only
/// rasterise these points). All coordinates are in screen space; <c>center</c> is the centre of the
/// tile's diamond footprint.
/// </summary>
internal static class IsoShapes
{
    /// <summary>
    /// The four corners of a 2:1-style diamond of the given footprint, clockwise from the top:
    /// top, right, bottom, left.
    /// </summary>
    public static Vector2[] DiamondCorners(Vector2 center, float tileW, float tileH)
    {
        float hw = tileW * 0.5f;
        float hh = tileH * 0.5f;
        return
        [
            new Vector2(center.X, center.Y - hh), // 0 top
            new Vector2(center.X + hw, center.Y), // 1 right
            new Vector2(center.X, center.Y + hh), // 2 bottom
            new Vector2(center.X - hw, center.Y), // 3 left
        ];
    }

    /// <summary>
    /// The two visible vertical faces of a block standing on the tile at <paramref name="baseCenter"/>,
    /// extruded up the screen by <paramref name="height"/> pixels. Each face is a 4-corner quad. The
    /// left face is the front-left side (under the left/bottom edge); the right face is the front-right
    /// side (under the bottom/right edge). The top diamond sits at <c>baseCenter - (0, height)</c>.
    /// </summary>
    public static (Vector2[] Left, Vector2[] Right) BlockFaces(Vector2 baseCenter, float tileW, float tileH, float height)
    {
        Vector2[] ground = DiamondCorners(baseCenter, tileW, tileH);
        Vector2[] top = DiamondCorners(new Vector2(baseCenter.X, baseCenter.Y - height), tileW, tileH);

        // Corner order: 0 top, 1 right, 2 bottom, 3 left.
        Vector2[] left = [top[3], top[2], ground[2], ground[3]];
        Vector2[] right = [top[2], top[1], ground[1], ground[2]];
        return (left, right);
    }

    /// <summary>
    /// <paramref name="segments"/> points evenly spaced around an axis-aligned ellipse of the given
    /// radii, starting at angle 0 (the rightmost point). Used to stroke iso ellipses (range rings).
    /// </summary>
    public static Vector2[] EllipsePoints(Vector2 center, float radiusX, float radiusY, int segments)
    {
        if (segments < 3) segments = 3;
        var points = new Vector2[segments];
        float step = MathHelper.TwoPi / segments;
        for (int i = 0; i < segments; i++)
        {
            float angle = i * step;
            points[i] = new Vector2(
                center.X + MathF.Cos(angle) * radiusX,
                center.Y + MathF.Sin(angle) * radiusY);
        }
        return points;
    }
}
