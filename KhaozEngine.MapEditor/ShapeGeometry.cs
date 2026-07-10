using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free geometry + editing helpers for the exclusion / region shape DTOs, shared by the overlay pick
/// and the transform gizmo so both agree on a shape's center and on how a move / scale gesture rewrites it. Disc
/// and rect are gizmo-editable; polygon shapes are read-only v1 (no move or resize), so the editing helpers return
/// <c>null</c> for them and <see cref="IsGizmoEditable"/> reports false.</summary>
internal static class ShapeGeometry
{
    /// <summary>A representative XZ center for a shape: the disc center, the rect midpoint, or the polygon point
    /// centroid. Returns false (and a zero center) for a null-pointless shape (a polygon with no points) or an
    /// unknown type.</summary>
    internal static bool TryCenter(MapShapeDoc shape, out float x, out float z)
    {
        switch (shape)
        {
            case DiscShapeDoc d:
                x = d.CenterX; z = d.CenterZ; return true;
            case RectShapeDoc r:
                x = (r.MinX + r.MaxX) * 0.5f; z = (r.MinZ + r.MaxZ) * 0.5f; return true;
            case PolygonShapeDoc p when p.Points.Count > 0:
            {
                float sx = 0f, sz = 0f;
                foreach (float[] pt in p.Points)
                {
                    sx += pt.Length > 0 ? pt[0] : 0f;
                    sz += pt.Length > 1 ? pt[1] : 0f;
                }
                x = sx / p.Points.Count; z = sz / p.Points.Count; return true;
            }
            default:
                x = 0f; z = 0f; return false;
        }
    }

    /// <summary>True when the transform gizmo can move / resize the shape: the disc and rect kinds only (polygon is
    /// read-only v1).</summary>
    internal static bool IsGizmoEditable(MapShapeDoc shape) => shape is DiscShapeDoc or RectShapeDoc;

    /// <summary>A new disc / rect shape translated by (<paramref name="dx"/>, <paramref name="dz"/>) on the XZ
    /// plane. Null for a polygon or unknown shape (not gizmo-movable v1).</summary>
    internal static MapShapeDoc? Translated(MapShapeDoc start, float dx, float dz) => start switch
    {
        DiscShapeDoc d => new DiscShapeDoc { CenterX = d.CenterX + dx, CenterZ = d.CenterZ + dz, Radius = d.Radius },
        RectShapeDoc r => new RectShapeDoc { MinX = r.MinX + dx, MinZ = r.MinZ + dz, MaxX = r.MaxX + dx, MaxZ = r.MaxZ + dz },
        _ => null,
    };

    /// <summary>A new disc / rect shape scaled by <paramref name="factor"/> about its own center: a disc scales its
    /// radius, a rect scales its extents around the midpoint. Null for a polygon or unknown shape.</summary>
    internal static MapShapeDoc? Scaled(MapShapeDoc start, float factor)
    {
        switch (start)
        {
            case DiscShapeDoc d:
                return new DiscShapeDoc { CenterX = d.CenterX, CenterZ = d.CenterZ, Radius = d.Radius * factor };
            case RectShapeDoc r:
            {
                float cx = (r.MinX + r.MaxX) * 0.5f, cz = (r.MinZ + r.MaxZ) * 0.5f;
                float halfX = (r.MaxX - r.MinX) * 0.5f * factor, halfZ = (r.MaxZ - r.MinZ) * 0.5f * factor;
                return new RectShapeDoc { MinX = cx - halfX, MinZ = cz - halfZ, MaxX = cx + halfX, MaxZ = cz + halfZ };
            }
            default:
                return null;
        }
    }
}
