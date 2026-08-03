namespace KhaozEngine.MapDoc;

/// <summary>GPU-free geometry helpers for the shape DTOs (<see cref="MapShapeDoc"/>), shared by the document
/// runtime and by editor tooling so a shape's representative center is derived one way everywhere. It sits in
/// the document package rather than the editor because the region runtime's nearest-center tiebreak ships to
/// games, and a second copy of the center rule is exactly how editor picking and game runtime would drift
/// apart.</summary>
public static class MapShapeGeometry
{
    /// <summary>A representative XZ center for a shape: the disc center, the rect midpoint, or the polygon point
    /// centroid. Returns false (and a zero center) for a null or pointless shape (a polygon with no points) or an
    /// unknown type, so callers can decide rather than have a guessed center handed to them.</summary>
    public static bool TryCenter(MapShapeDoc? shape, out float centerX, out float centerZ)
    {
        switch (shape)
        {
            case DiscShapeDoc d:
                centerX = d.CenterX; centerZ = d.CenterZ; return true;
            case RectShapeDoc r:
                centerX = (r.MinX + r.MaxX) * 0.5f; centerZ = (r.MinZ + r.MaxZ) * 0.5f; return true;
            case PolygonShapeDoc p when p.Points.Count > 0:
            {
                float sx = 0f, sz = 0f;
                foreach (float[] pt in p.Points)
                {
                    sx += pt.Length > 0 ? pt[0] : 0f;
                    sz += pt.Length > 1 ? pt[1] : 0f;
                }
                centerX = sx / p.Points.Count; centerZ = sz / p.Points.Count; return true;
            }
            default:
                centerX = 0f; centerZ = 0f; return false;
        }
    }
}
