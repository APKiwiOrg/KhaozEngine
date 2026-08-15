using System;

namespace KhaozEngine.TileWorld;

/// <summary>The one diagonal-split rule, shared by the raycast here and the ground mesher in
/// <c>KhaozEngine.TileWorld.Render3D</c>, so a click lands on the triangle that is drawn. Corners: h00 SW,
/// h10 SE, h01 NW, h11 NE.</summary>
public static class TileTriangulation
{
    /// <summary>True when the tile splits SW to NE (triangles SW-SE-NE and SW-NE-NW), false when NW to SE
    /// (triangles SW-SE-NW and SE-NE-NW). A <see cref="TileOverlayShape.DiagonalHalf"/> overlay forces the
    /// split (even rotation SW-NE, odd NW-SE), otherwise the diagonal whose corners differ least in height
    /// wins, which removes saddle artifacts and is deterministic.</summary>
    public static bool SplitSwNe(short h00, short h10, short h01, short h11, TileOverlayShape shape, int overlayRotation)
    {
        if (shape == TileOverlayShape.DiagonalHalf) return (overlayRotation & 1) == 0;
        return Math.Abs(h00 - h11) <= Math.Abs(h10 - h01);
    }
}
