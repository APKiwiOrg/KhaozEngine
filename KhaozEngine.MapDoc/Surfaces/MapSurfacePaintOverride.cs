using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Applies paint only when it can be represented on the unchanged physical faces.</summary>
internal static class MapSurfacePaintOverride
{
    internal static MapCompiledPatch Apply(MapCompiledPatch target, MapSurfaceRef surface, MapSurfacePatch patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Role != MapSurfaceRole.PaintOverride || surface.PaintTargetSurfaceId != target.Key.SurfaceId)
            throw new MapDocumentException("paint override must name its target surface");
        // This temporary triangulation describes paint regions. No face from it is returned as support.
        MapCompiledPatch regions = MapSurfaceCompiler.Compile(surface with { Role = MapSurfaceRole.SupportFloor }, patch);
        try
        {
            var byCell = new Dictionary<int, List<int>>();
            for (int i = 0; i < regions.Faces.Count; i++)
            {
                int cell = regions.Faces[i].Key.Primitive;
                if (!byCell.TryGetValue(cell, out List<int>? indices)) byCell.Add(cell, indices = new());
                indices.Add(i);
            }
            var paint = new MapPaintCoverage[target.Faces.Count];
            for (int i = 0; i < target.Faces.Count; i++)
            {
                MapCompiledFace face = target.Faces[i];
                MapExactTriangle triangle = target.ExactTriangle(face);
                var bounds = Bounds(surface, patch, triangle);
                MapExactValue covered = default;
                MapPaintCoverage? selected = null;
                for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
                    for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                    {
                        if (!byCell.TryGetValue(z * 64 + x, out List<int>? candidates)) continue;
                        foreach (int index in candidates)
                        {
                            MapExactValue area = IntersectionArea(triangle, regions.ExactTriangle(regions.Faces[index]));
                            if (area.Sign == 0) continue;
                            MapPaintCoverage next = regions.Paint[index] with { Face = face.Key };
                            if (selected is { } previous && previous != next)
                                throw new MapDocumentException("paint override boundary crosses physical face");
                            selected = next;
                            covered = covered.Add(area);
                        }
                    }
                if (selected is not null && covered != Unsigned(MapSubdividedTriangle.Cross(triangle.A, triangle.B, triangle.C)).Divide(new(2, 1)))
                    throw new MapDocumentException("paint override presence boundary crosses physical face");
                paint[i] = selected ?? target.Paint[i];
            }
            return new(target.Key, target.Role, target.Anchor, target.VertexIds, target.ExactVertices,
                target.Offsets, target.Faces, paint, target.LegacyFallbackCells);
        }
        catch (MapExactOverflowException error)
        {
            throw new MapDocumentException("paint override is not representable", error);
        }
    }

    static (int MinX, int MinZ, int MaxX, int MaxZ) Bounds(MapSurfaceRef surface, MapSurfacePatch patch,
        MapExactTriangle triangle)
    {
        MapExactValue unit = surface.Frame.CellUnitMetres.Exact();
        MapLatticeAddress origin = patch.CornerAddress(0, 0);
        (MapExactValue X, MapExactValue Z) Local(MapExactPoint point)
        {
            MapExactValue z = point.Z.Divide(unit);
            if (surface.Frame.RowDirection == MapRowDirection.NegativeZ) z = z.Negate();
            return (point.X.Divide(unit).Subtract(new(origin.X, 1)), z.Subtract(new(origin.Z, 1)));
        }
        var a = Local(triangle.A);
        var b = Local(triangle.B);
        var c = Local(triangle.C);
        long minX = Math.Min(a.X.Floor(), Math.Min(b.X.Floor(), c.X.Floor()));
        long minZ = Math.Min(a.Z.Floor(), Math.Min(b.Z.Floor(), c.Z.Floor()));
        long maxX = Math.Max(a.X.Floor(), Math.Max(b.X.Floor(), c.X.Floor()));
        long maxZ = Math.Max(a.Z.Floor(), Math.Max(b.Z.Floor(), c.Z.Floor()));
        return ((int)Math.Clamp(minX, 0, patch.Width) + patch.CellMinX,
            (int)Math.Clamp(minZ, 0, patch.Depth) + patch.CellMinZ,
            (int)Math.Clamp(maxX, -1, patch.Width - 1) + patch.CellMinX,
            (int)Math.Clamp(maxZ, -1, patch.Depth - 1) + patch.CellMinZ);
    }

    static MapExactValue IntersectionArea(MapExactTriangle subject, MapExactTriangle clip)
    {
        Span<MapExactPoint> polygon = stackalloc MapExactPoint[8];
        Span<MapExactPoint> scratch = stackalloc MapExactPoint[8];
        polygon[0] = subject.A;
        polygon[1] = subject.B;
        polygon[2] = subject.C;
        int count = 3;
        int winding = MapSubdividedTriangle.Cross(clip.A, clip.B, clip.C).Sign;
        count = Clip(polygon, scratch, count, winding, clip.A, clip.B);
        count = Clip(polygon, scratch, count, winding, clip.B, clip.C);
        count = Clip(polygon, scratch, count, winding, clip.C, clip.A);
        MapExactValue area = default;
        for (int i = 1; i + 1 < count; i++)
            area = area.Add(Unsigned(MapSubdividedTriangle.Cross(polygon[0], polygon[i], polygon[i + 1])));
        return area.Divide(new(2, 1));

    }

    static int Clip(Span<MapExactPoint> polygon, Span<MapExactPoint> scratch, int count, int winding,
        MapExactPoint a, MapExactPoint b)
    {
        if (count == 0) return 0;
        int next = 0;
        MapExactPoint previous = polygon[count - 1];
        MapExactValue previousDistance = MapSubdividedTriangle.Cross(a, b, previous);
        bool previousInside = previousDistance.Sign * winding >= 0;
        for (int i = 0; i < count; i++)
        {
            MapExactPoint current = polygon[i];
            MapExactValue distance = MapSubdividedTriangle.Cross(a, b, current);
            bool inside = distance.Sign * winding >= 0;
            if (inside != previousInside)
            {
                MapExactValue t = previousDistance.Divide(previousDistance.Subtract(distance));
                scratch[next++] = new(previous.X.Add(current.X.Subtract(previous.X).Multiply(t)), default,
                    previous.Z.Add(current.Z.Subtract(previous.Z).Multiply(t)));
            }
            if (inside) scratch[next++] = current;
            previous = current;
            previousDistance = distance;
            previousInside = inside;
        }
        scratch[..next].CopyTo(polygon);
        return next;
    }

    static MapExactValue Unsigned(MapExactValue value) => value.Sign < 0 ? value.Negate() : value;
}
