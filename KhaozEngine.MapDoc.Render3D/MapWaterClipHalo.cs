using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>A render-only bounded dependency lookup. Canonical meshes remain look-free. Clip domains are unions
/// of complete free-surface cells near each mesh, evaluated by the shader at displaced world positions.</summary>
internal sealed class MapWaterClipHalo
{
    sealed record Body(MapWaterResourceCell[] Cells, MapBoundsIndex Index);
    readonly Dictionary<string, Body> _bodies = new(StringComparer.Ordinal);
    readonly Dictionary<MapWaterCellKey, MapWaterResourceCell> _cells = new();
    readonly double _halo;
    readonly MapSceneAdmission _admission;
    readonly MapWaterWorkBudget _pairs = new(MapWaterDomain.MaxPairChecks, compile: true);

    internal MapWaterClipHalo(MapScopedWaterResult water, double halo, MapSceneAdmission admission)
    {
        _halo = halo; _admission = admission;
        admission.ReserveBytes(checked(256L + 512L * water.AcquiredCells.Count));
        foreach (var cell in water.AcquiredCells)
        {
            if (_cells.TryGetValue(cell.Key, out var prior))
            {
                if (prior.PayloadDigest != cell.PayloadDigest) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Acquired water cell digests disagree.");
                continue;
            }
            _cells.Add(cell.Key, cell);
        }
        foreach (var body in _cells.Values.GroupBy(c => c.Key.BodyId, StringComparer.Ordinal))
        {
            var cells = body.OrderBy(c => c.Key.SpaceId, StringComparer.Ordinal).ThenBy(c => c.Key.FragmentId, StringComparer.Ordinal)
                .ThenBy(c => c.Key.StorageOwner.X).ThenBy(c => c.Key.StorageOwner.Z).ThenBy(c => c.Key.X).ThenBy(c => c.Key.Z).ToArray();
            _bodies.Add(body.Key, new(cells, new(cells.Select(c => new MapWaterPolygons.Bounds(
                Point(c.CellBounds.MinX, c.CellBounds.MinZ), Point(c.CellBounds.MaxX, c.CellBounds.MaxZ))).ToArray())));
        }
    }

    internal (byte[] Data, string Digest) Build(MapWaterResourceCell cell, MapSubmissionAnchor anchor)
    {
        if (!_cells.TryGetValue(cell.Key, out var acquired) || acquired.PayloadDigest != cell.PayloadDigest)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Draw water has no identical acquired canonical cell.");
        var bounds = acquired.CellBounds;
        var body = _bodies[cell.Key.BodyId];
        var query = new MapWaterPolygons.Bounds(Point(bounds.MinX - _halo, bounds.MinZ - _halo), Point(bounds.MaxX + _halo, bounds.MaxZ + _halo));
        var candidates = body.Index.Overlapping(query, _pairs, _admission);
        _admission.ReserveBytes(checked(64L + candidates.Count * 8L));
        var indices = candidates.Where(i => Touches(body.Cells[i], query)).ToArray();
        long count = 0;
        foreach (int index in indices)
            foreach (var region in body.Cells[index].Regions)
            {
                if (region.Kind == MapWaterRegionKind.Unresolved)
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Water clip dependencies contain an unresolved cell.");
                if (region.Kind == MapWaterRegionKind.FreeSurface) count = checked(count + region.Triangles.Count);
            }
        if (count == 0 || count > WaterSurfaceDomain.MaxVertices / 3) throw new MapSceneCapacityException(nameof(WaterSurfaceDomain.MaxVertices), checked(count * 3L), WaterSurfaceDomain.MaxVertices);
        _admission.ReserveCompilation(count);
        _admission.ReserveBytes(checked(512L + count * (3 * 8L + 3 * 16L * 3)));
        var points = new Vector2[checked((int)count * 3)];
        int next = 0;
        foreach (int index in indices)
            foreach (var triangle in body.Cells[index].Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).SelectMany(r => r.Triangles))
                foreach (var point in new[] { triangle.A, triangle.B, triangle.C })
                {
                    var offset = MapSubmissionGeometry.Offset(point, anchor);
                    points[next++] = new(offset.X, offset.Z);
                }
        string digest = MapWaterJson.Hash("kemap/native-water-halo/1", writer =>
        {
            writer.WriteStartArray(); writer.WriteStringValue(cell.PayloadDigest);
            writer.WriteNumberValue(BitConverter.DoubleToInt64Bits(_halo));
            writer.WriteNumberValue(anchor.X); writer.WriteNumberValue(anchor.Y); writer.WriteNumberValue(anchor.Z);
            foreach (int index in indices)
            {
                var neighbour = body.Cells[index];
                writer.WriteStringValue(MapWaterScopedCompiler.CellId(neighbour.Key)); writer.WriteStringValue(neighbour.PayloadDigest);
            }
            writer.WriteEndArray();
        });
        return (WaterSurfaceDomain.Encode(points), digest);
    }

    bool Touches(MapWaterResourceCell cell, MapWaterPolygons.Bounds halo)
    {
        // A resident cell can have wet geometry wholly beyond this halo. Reading it would make this resource's
        // bytes and digest depend on unrelated window memberships. Test actual original-domain coverage.
        foreach (var region in cell.Regions)
            foreach (var triangle in region.Triangles)
            {
                _pairs.Charge(1); _admission.ReserveBytes(512);
                if (MapWaterPolygons.ClipToRect(new[] { MapWidePoint.From(triangle.A).Xz, MapWidePoint.From(triangle.C).Xz,
                    MapWidePoint.From(triangle.B).Xz }, halo.Min, halo.Max).Count != 0) return true;
            }
        return false;
    }

    static MapWideXz Point(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new OverflowException();
        return new(MapWideRational.FromDouble(x), MapWideRational.FromDouble(z));
    }
}
