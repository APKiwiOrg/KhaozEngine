using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact spatial operations shared by occupied-space queries and validation.</summary>
internal static class MapSpaceGeometry
{
    internal static IEnumerable<MapTopologyRecord> Records(MapScopedSurfaces view)
    {
        foreach (MapRecordRef reference in view.Witness.Records)
            if (view.TryRecord(reference, out MapTopologyRecord? record, out _, borrow: true) && record is not null)
                yield return record;
    }

    internal static T Record<T>(MapScopedSurfaces view, MapRecordRef reference) where T : MapTopologyRecord =>
        view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) && record is T typed
            ? typed : throw new MapDocumentException($"missing geometry: record '{reference.Id}' {reference.Anchor} ({status})");

    internal static MapSurfaceRef Surface(MapScopedSurfaces view, string id) =>
        view.Surfaces.FirstOrDefault(s => s.Id == id) ?? throw new MapDocumentException($"missing geometry: surface '{id}'");

    internal static (long X, long Z) Cell(MapLatticeFrame frame, MapExactXz point)
    {
        MapExactValue unit = frame.CellUnitMetres.Exact();
        MapExactValue z = frame.RowDirection == MapRowDirection.NegativeZ ? point.Z.Negate() : point.Z;
        return (point.X.Divide(unit).Floor(), z.Divide(unit).Floor());
    }

    internal static int? FootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint, MapExactXz point)
    {
        foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(view, footprint.Lattice.SurfaceId, point))
        {
            MapPatchKey key = MapPatchKey.ForCell(footprint.Lattice.SurfaceId, x, z);
            int slot = MapLowerCellClassifier.SlotCell(x, z);
            if (key == footprint.Lattice && footprint.SlotCells.Contains(slot)) return slot;
        }
        return null;
    }

    internal static MapExactXz Xz(MapExactPoint point) => new(point.X, point.Z);
    internal static MapExactValue Min(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    internal static MapExactValue Max(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
    internal static bool Between(MapExactValue value, MapExactValue a, MapExactValue b) =>
        value.CompareTo(Min(a, b)) >= 0 && value.CompareTo(Max(a, b)) <= 0;
    internal static bool OnSegment(MapExactXz point, MapExactXz a, MapExactXz b) => a != b &&
        MapSubdividedTriangle.CrossWide(new(a.X, default, a.Z), new(b.X, default, b.Z), new(point.X, default, point.Z)).Sign == 0 &&
        Between(point.X, a.X, b.X) && Between(point.Z, a.Z, b.Z);

    internal static MapExactValue Interpolate(MapExactPoint a, MapExactPoint b, MapExactXz point)
    {
        var t = a.X != b.X ? MapWideRational.From(point.X).Subtract(MapWideRational.From(a.X)).Divide(MapWideRational.From(b.X).Subtract(MapWideRational.From(a.X)))
            : MapWideRational.From(point.Z).Subtract(MapWideRational.From(a.Z)).Divide(MapWideRational.From(b.Z).Subtract(MapWideRational.From(a.Z)));
        return MapSubdividedTriangle.Narrow(MapWideRational.From(a.Y).Add(MapWideRational.From(b.Y).Subtract(MapWideRational.From(a.Y)).Multiply(t)));
    }

    internal static MapExactValue? ChainHeight(IReadOnlyList<MapExactPoint> points, MapExactXz point)
    {
        for (int i = 0; i + 1 < points.Count; i++)
            if (OnSegment(point, Xz(points[i]), Xz(points[i + 1]))) return Interpolate(points[i], points[i + 1], point);
        return null;
    }

    internal static MapExactValue? Height(IReadOnlyList<MapBoundFace> faces, MapExactXz point)
    {
        foreach (MapBoundFace face in faces)
            if (MapSubdividedTriangle.Height(face.Triangle, point.X, point.Z) is { } height) return height;
        return null;
    }

    /// <summary>The lexicographic derivative in the R4 world direction (1, infinitesimal +Z).</summary>
    internal static int Direction(IReadOnlyList<MapBoundFace> faces, MapExactXz point)
    {
        MapBoundFace? selected = null;
        foreach (var face in faces)
        {
            if (MapSubdividedTriangle.Height(face.Triangle, point.X, point.Z) is null) continue;
            selected ??= face;
            var polygon = MapRefinementPolygon.Triangle(face.Triangle).Select(MapWideXz.From).ToArray();
            if (MapWaterPolygons.RingContains(polygon, MapWideXz.From(point))) { selected = face; break; }
        }
        if (selected is not { } bound) return 0;
        var t = bound.Triangle;
        var dx1 = W(t.B.X).Subtract(W(t.A.X)); var dx2 = W(t.C.X).Subtract(W(t.A.X));
        var dz1 = W(t.B.Z).Subtract(W(t.A.Z)); var dz2 = W(t.C.Z).Subtract(W(t.A.Z));
        var dy1 = W(t.B.Y).Subtract(W(t.A.Y)); var dy2 = W(t.C.Y).Subtract(W(t.A.Y));
        var area = dx1.Multiply(dz2).Subtract(dz1.Multiply(dx2));
        var x = dy1.Multiply(dz2).Subtract(dy2.Multiply(dz1)).Divide(area);
        return x.Sign != 0 ? x.Sign : dx1.Multiply(dy2).Subtract(dx2.Multiply(dy1)).Divide(area).Sign;
        static MapWideRational W(MapExactValue value) => MapWideRational.From(value);
    }

    internal static string Owner(MapScopedSurfaces view, MapSpaceDoc space)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (space.AliasOf is { } alias)
        {
            if (!seen.Add(space.Id)) throw new MapDocumentException("invalid space alias cycle");
            space = Record<MapSpaceDoc>(view, alias);
        }
        return space.Id;
    }

    internal static IReadOnlyList<MapSpaceDoc> Ancestors(MapScopedSurfaces view, MapSpaceDoc space)
    {
        var result = new List<MapSpaceDoc>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!seen.Add(space.Id)) throw new MapDocumentException("invalid space parent cycle");
            result.Add(space);
            if (space.Parent is not { } parent) return result.AsReadOnly();
            space = Record<MapSpaceDoc>(view, parent);
        }
    }

    /// <summary>Enumerates borrowed payloads without materializing another patch collection.</summary>
    internal sealed class Patches(MapScopedSurfaces view) : IReadOnlyCollection<MapSurfacePatch>
    {
        public int Count => view.Witness.Present.Count;
        public IEnumerator<MapSurfacePatch> GetEnumerator()
        {
            foreach (var entry in view.Witness.Present)
            {
                if (!view.TryAcquiredPatch(entry.Key, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
                    throw new MapDocumentException($"missing geometry: patch {entry.Key} ({status})");
                yield return patch;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
