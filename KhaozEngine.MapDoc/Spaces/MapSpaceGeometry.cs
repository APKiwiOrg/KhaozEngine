using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

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
        var (x, z) = Cell(Surface(view, footprint.Lattice.SurfaceId).Frame, point);
        MapPatchKey key = MapPatchKey.ForCell(footprint.Lattice.SurfaceId, x, z);
        int slot = MapLowerCellClassifier.SlotCell(x, z);
        return key == footprint.Lattice && footprint.SlotCells.Contains(slot) ? slot : null;
    }

    internal static MapExactXz Xz(MapExactPoint point) => new(point.X, point.Z);
    internal static MapExactValue Min(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    internal static MapExactValue Max(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
    internal static bool Between(MapExactValue value, MapExactValue a, MapExactValue b) =>
        value.CompareTo(Min(a, b)) >= 0 && value.CompareTo(Max(a, b)) <= 0;
    internal static bool OnSegment(MapExactXz point, MapExactXz a, MapExactXz b) => a != b &&
        MapSubdividedTriangle.Cross(new(a.X, default, a.Z), new(b.X, default, b.Z), new(point.X, default, point.Z)).Sign == 0 &&
        Between(point.X, a.X, b.X) && Between(point.Z, a.Z, b.Z);

    internal static MapExactValue Interpolate(MapExactPoint a, MapExactPoint b, MapExactXz point)
    {
        MapExactValue t = a.X != b.X ? point.X.Subtract(a.X).Divide(b.X.Subtract(a.X))
            : point.Z.Subtract(a.Z).Divide(b.Z.Subtract(a.Z));
        return a.Y.Add(b.Y.Subtract(a.Y).Multiply(t));
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
