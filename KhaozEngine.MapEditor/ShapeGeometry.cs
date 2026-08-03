using System;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free geometry + editing helpers for the exclusion / region shape DTOs, shared by the overlay pick
/// and the transform gizmo so both agree on a shape's center and on how a move / scale gesture rewrites it. Disc
/// and rect are gizmo-editable; polygon shapes are read-only v1 (no move or resize), so the editing helpers return
/// <c>null</c> for them and <see cref="IsGizmoEditable"/> reports false.</summary>
internal static class ShapeGeometry
{
    /// <summary>A representative XZ center for a shape: the disc center, the rect midpoint, or the polygon point
    /// centroid. Returns false (and a zero center) for a null-pointless shape (a polygon with no points) or an
    /// unknown type. The rule itself lives in <see cref="MapShapeGeometry.TryCenter"/> now, because the region
    /// runtime ships it to games, and this stays as the editor-local name its gizmo call sites already use.</summary>
    internal static bool TryCenter(MapShapeDoc shape, out float x, out float z)
        => MapShapeGeometry.TryCenter(shape, out x, out z);

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

    /// <summary>Base margin (world units) padded around a shape's exact bounds for the dirty-region rect an
    /// exclusion or scatter-override edit invalidates. Both only ever reach scatter
    /// (<c>PropScatter.InExclusion</c> / <c>OverrideFor</c>, a pointwise <see cref="IArea2D.Contains"/> test with
    /// no falloff and no height/normal reach), but scatter's chunk assignment and its membership test disagree by
    /// the layer's jitter: <c>PropScatter.Generate</c> assigns a candidate to a chunk by its UN-jittered cell
    /// centre (the half-open [Min, Max) window test) while testing exclusion / override membership at the
    /// JITTERED position, so a candidate whose cell centre sits up to the layer's Jitter outside the shape can
    /// still flip its cull result while living in a chunk beyond the bare shape bounds. The true margin floor is
    /// therefore the document's largest scatter jitter (authored jitter has no validator clamp, so it can exceed
    /// any constant), which the shape commands capture at Apply time via <see cref="BoundsMarginFor"/> and pad
    /// with instead of this constant alone. The constant itself covers only the jitter-free boundary effects:
    /// chunk invalidation maps a world rect to the whole chunks it touches (inclusive of a shape edge sitting
    /// exactly on a chunk seam), and a disc/rect authored with a boundary exactly on a seam can drift either way
    /// by float rounding. It stays well under <see cref="FeatureGeometry.FootprintMargin"/>'s 8 m, which pads a
    /// height/normal reach this shape-only case does not have.</summary>
    internal const float ShapeBoundsMargin = 2f;

    /// <summary>The dirty-region margin for a shape edit against <paramref name="doc"/>:
    /// <see cref="ShapeBoundsMargin"/> plus the largest scatter-layer jitter in the document (the margin floor,
    /// see the constant's doc for why jitter reaches beyond the shape). Absolute value per layer: a degenerate
    /// negative-authored jitter displaces candidates by the same magnitude (the Jitter field has no clamp). A
    /// document with no scatter layers pads by the bare constant (no scatter means nothing can flip, the rect is
    /// already conservative).</summary>
    internal static float BoundsMarginFor(MapDocument doc)
    {
        float jitter = 0f;
        foreach (MapScatterLayer layer in doc.ScatterLayers)
            jitter = MathF.Max(jitter, MathF.Abs(layer.Jitter));
        return ShapeBoundsMargin + jitter;
    }

    /// <summary>A conservative world-space AABB covering <paramref name="shape"/>, padded by
    /// <see cref="ShapeBoundsMargin"/> only. Doc-independent convenience overload: command dirty regions pass
    /// their captured <see cref="BoundsMarginFor"/> margin to the explicit overload instead, since the bare
    /// constant does not cover scatter jitter.</summary>
    internal static bool TryBounds(MapShapeDoc? shape, out RectArea area) =>
        TryBounds(shape, ShapeBoundsMargin, out area);

    /// <summary>A conservative world-space AABB covering <paramref name="shape"/>, padded by
    /// <paramref name="margin"/>: a disc's center +/- radius, a rect's Min/Max (normalized if authored with
    /// Min &gt; Max), or the min/max over a polygon's points. False (and a default area) for a null shape or an
    /// empty polygon (no points to bound), the same no-guessing rule <see cref="TryCenter"/> follows: a false
    /// here means the edit takes the full rebuild.</summary>
    internal static bool TryBounds(MapShapeDoc? shape, float margin, out RectArea area)
    {
        switch (shape)
        {
            case DiscShapeDoc d:
            {
                float r = MathF.Abs(d.Radius) + margin;
                area = new RectArea(d.CenterX - r, d.CenterZ - r, d.CenterX + r, d.CenterZ + r);
                return true;
            }
            case RectShapeDoc r:
            {
                float minX = MathF.Min(r.MinX, r.MaxX) - margin;
                float minZ = MathF.Min(r.MinZ, r.MaxZ) - margin;
                float maxX = MathF.Max(r.MinX, r.MaxX) + margin;
                float maxZ = MathF.Max(r.MinZ, r.MaxZ) + margin;
                area = new RectArea(minX, minZ, maxX, maxZ);
                return true;
            }
            case PolygonShapeDoc p when p.Points.Count > 0:
            {
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (float[] pt in p.Points)
                {
                    float x = pt.Length > 0 ? pt[0] : 0f;
                    float z = pt.Length > 1 ? pt[1] : 0f;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
                area = new RectArea(minX - margin, minZ - margin, maxX + margin, maxZ + margin);
                return true;
            }
            default:
                area = default;
                return false;
        }
    }
}
