using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The visible ground material at a foliage sample and the legacy material eligibility of a layer. The material
/// comes from three sources, highest first: the context's scoped partial paint partition on the selected canonical face,
/// then released world paint, which compiles whole-face coverage onto the target's faces, then the cell's own paint.
/// The visible material is the coverage's overlay where the selected triangle carries it, else its underlay.</summary>
internal sealed class MapFoliageMaterials
{
    readonly Dictionary<ushort, MapFoliageMaterialKind> _kinds = new();
    readonly HashSet<ushort>? _allowed;
    readonly bool _excludeIndoors;
    readonly Dictionary<MapFaceKey, List<MapPaintRegion>> _regions = new();

    internal MapFoliageMaterials(MapFoliageLayer layer, IReadOnlyDictionary<string, MapPaintRegionSet> partial)
    {
        foreach (MapFoliageMaterial material in layer.Materials) _kinds.TryAdd(material.Id, material.Kind);
        _allowed = layer.AllowedVisibleMaterials.Count == 0 ? null : layer.AllowedVisibleMaterials.ToHashSet();
        _excludeIndoors = layer.ExcludeIndoors;
        foreach (MapPaintRegionSet set in partial.Values)
            foreach (MapPaintRegion region in set.Regions)
            {
                if (!_regions.TryGetValue(region.Face, out List<MapPaintRegion>? list)) _regions.Add(region.Face, list = new());
                list.Add(region);
            }
    }

    /// <summary>The coverage at <paramref name="point"/> on <paramref name="face"/>: the first published partial region of
    /// that face whose closed polygon holds the point, else <paramref name="painted"/>, the face's world-painted or own
    /// coverage. Unpainted notches inside a partial source keep the face coverage.</summary>
    internal MapPaintCoverage Coverage(MapFaceKey face, MapPaintCoverage painted, MapExactXz point)
    {
        if (!_regions.TryGetValue(face, out List<MapPaintRegion>? regions)) return painted;
        foreach (MapPaintRegion region in regions)
            if (Contains(region.Polygon, point)) return region.Material;
        return painted;
    }

    /// <summary>The released tile eligibility after a positive raster: a zero underlay, a visible material outside a
    /// non-empty allowed list, a water material, then an Indoor cell when the layer excludes indoors. NoDraw and Blocked
    /// are ignored, as the released predicate ignores them.</summary>
    internal MapFoliageReason Reason(MapPaintCoverage coverage, MapCellFlags flags, out ushort visible)
    {
        visible = coverage.OverlayCovers && coverage.Overlay != 0 ? coverage.Overlay : coverage.Underlay;
        if (coverage.Underlay == 0) return MapFoliageReason.MissingUnderlay;
        if (_allowed is not null && !_allowed.Contains(visible)) return MapFoliageReason.MaterialNotAllowed;
        if (_kinds.TryGetValue(visible, out MapFoliageMaterialKind kind) && kind == MapFoliageMaterialKind.Water)
            return MapFoliageReason.WaterMaterial;
        if (_excludeIndoors && (flags & MapCellFlags.Indoor) != 0) return MapFoliageReason.Indoor;
        return MapFoliageReason.Accepted;
    }

    /// <summary>The released world paint sources acquired for <paramref name="targetId"/>: PaintOverride surfaces without
    /// an effective pose, ordered lowest precedence first so the highest priority, then the first ordinal ID, applies
    /// last.</summary>
    internal static MapSurfaceRef[] WorldPaint(MapScopedSurfaces view, string targetId) => view.Surfaces
        .Where(s => s.Role == MapSurfaceRole.PaintOverride && s.PaintTargetSurfaceId == targetId &&
            view.EffectiveContext?.IsPartialPaint(s) != true)
        .OrderBy(s => s.PaintPriority).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Applies every acquired world paint patch to the local target faces, through the released whole-face
    /// adapter, in precedence order.</summary>
    internal static MapCompiledPatch ApplyWorldPaint(MapScopedSurfaces view, MapCompiledPatch target, IReadOnlyList<MapSurfaceRef> sources)
    {
        foreach (MapSurfaceRef source in sources)
            foreach (MapPatchKey key in view.Witness.Present.Select(p => p.Key).Where(k => k.SurfaceId == source.Id).Order())
                if (view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) && patch is not null)
                    target = MapSurfaceCompiler.ApplyPaintOverride(target, source, patch);
        return target;
    }

    /// <summary>The released overlay containment test at a cell-local point: true when any overlay triangle of the
    /// authored cut contains the point within 1e-6. Neighbour eligibility reads it at the cell centre.</summary>
    internal static bool OverlayAt(MapSurfaceCell cell, bool splitSwNe, float localX, float localZ)
    {
        Span<MapLatticeTriangle> triangles = stackalloc MapLatticeTriangle[4];
        int count = MapSurfaceTopology.Triangulate(cell.Cut, cell.Rotation, splitSwNe, triangles);
        var point = new Vector2(localX, localZ);
        for (int i = 0; i < count; i++)
        {
            MapLatticeTriangle t = triangles[i];
            if (t.Overlay && Contains(point, Local(t.A), Local(t.B), Local(t.C))) return true;
        }
        return false;
    }

    static Vector2 Local(MapLatticePoint point)
    {
        (int x, int z) = MapSurfaceTopology.LocalTwice(point);
        return new(x * 0.5f, z * 0.5f);
    }

    static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float ab = Cross(b - a, p - a);
        float bc = Cross(c - b, p - b);
        float ca = Cross(a - c, p - c);
        return (ab >= -1e-6f && bc >= -1e-6f && ca >= -1e-6f) || (ab <= 1e-6f && bc <= 1e-6f && ca <= 1e-6f);
    }

    static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    // A closed convex region polygon in exact world XZ. Edge and vertex points belong to the region.
    static bool Contains(IReadOnlyList<MapExactPoint> polygon, MapExactXz point)
    {
        int sign = 0;
        MapWideXz p = MapWideXz.From(point);
        for (int i = 0; i < polygon.Count; i++)
        {
            MapWideXz a = MapWideXz.From(new(polygon[i].X, polygon[i].Z));
            MapWideXz b = MapWideXz.From(new(polygon[(i + 1) % polygon.Count].X, polygon[(i + 1) % polygon.Count].Z));
            int side = b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X))).Sign;
            if (side == 0) continue;
            if (sign != 0 && sign != side) return false;
            sign = side;
        }
        return polygon.Count >= 3;
    }
}
