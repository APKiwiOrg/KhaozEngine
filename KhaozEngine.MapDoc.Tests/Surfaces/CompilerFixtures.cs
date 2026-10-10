using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class CompilerFixtures
{
    internal static readonly MapSurfaceCell Full = new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch) OneCell(int sw, int se, int nw, int ne,
        MapSurfaceCell cell, MapSurfaceRole role = MapSurfaceRole.SupportFloor)
    {
        var surface = new MapSurfaceRef("one", new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ,
            MapHeightDatum.WorldY0), role, MapPresencePolicy.Native, null, null, "");
        var patch = new MapSurfacePatch
        {
            Key = new("one", 0, 0),
            Width = 1,
            Depth = 1,
            Heights = new[] { sw, se, nw, ne },
            Cells = new[] { cell },
            Presence = new ulong[] { 1 },
        };
        return (surface, patch);
    }

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch) Row(MapPresencePolicy policy,
        params (MapSurfaceCell Cell, bool Present)[] cells)
    {
        if (cells.Length is < 1 or > MapPatchKey.SlotCells)
            throw new ArgumentOutOfRangeException(nameof(cells));
        MapLatticeFrame frame = policy == MapPresencePolicy.LegacyTileWorld
            ? MapLatticeFrame.ImportedMetreCentimetre
            : new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var surface = new MapSurfaceRef("row", frame, MapSurfaceRole.SupportFloor, policy, null, null, "");
        var patch = new MapSurfacePatch
        {
            Key = new("row", 0, 0),
            Width = cells.Length,
            Depth = 1,
            Heights = new int[2 * (cells.Length + 1)],
            Cells = cells.Select(c => c.Cell).ToArray(),
            Presence = new ulong[(cells.Length + 63) / 64],
        };
        for (int x = 0; x < cells.Length; x++) patch.SetPresent(x, 0, cells[x].Present);
        return (surface, patch);
    }

    internal static MapSurfaceRef ThirdsSurface() => new("thirds",
        new(new(1, 3), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
        MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");

    internal static MapSurfacePatch ThirdsPatch() => new()
    {
        Key = new("thirds", -1, 0),
        CellMinX = 61,
        Width = 3,
        Depth = 1,
        Heights = new[] { 100, 150, 200, 250, 300, 350, 400, 450 },
        Cells = new[] { Full, Full, Full },
        Presence = new ulong[] { 7 },
    };

    internal static bool Has(MapCompiledPatch mesh, MapCompiledFace face, MapVertexId vertex)
        => mesh.VertexIds[face.A] == vertex || mesh.VertexIds[face.B] == vertex || mesh.VertexIds[face.C] == vertex;

    internal static MapExactPoint ExactAt(MapCompiledPatch mesh, string surfaceId, MapLatticeAddress address)
    {
        var vertex = new MapVertexId(surfaceId, address);
        for (int i = 0; i < mesh.VertexIds.Count; i++)
            if (mesh.VertexIds[i] == vertex) return mesh.ExactVertices[i];
        throw new InvalidOperationException("fixture vertex is missing");
    }

    internal static MapExactValue ExactArea(MapCompiledPatch mesh, MapCompiledFace face)
    {
        MapExactValue twice = CrossXz(mesh.ExactVertices[face.A], mesh.ExactVertices[face.B], mesh.ExactVertices[face.C]);
        return (twice.Sign < 0 ? twice.Negate() : twice).Divide(new(2, 1));
    }

    internal static MapExactValue Area(MapCompiledPatch mesh, int slotCell)
        => mesh.Faces.Where(f => f.Key.Primitive == slotCell)
            .Aggregate(new MapExactValue(0, 1), (area, face) => area.Add(ExactArea(mesh, face)));

    internal static IEnumerable<(MapExactPoint From, MapExactPoint To)> ChildEdges(MapCompiledPatch mesh,
        int slotCell, byte parent)
    {
        MapExactPoint[][] children = mesh.Faces.Where(f => f.Key.Primitive == slotCell && f.Key.ParentTriangle == parent)
            .Select(f => new[] { mesh.ExactVertices[f.A], mesh.ExactVertices[f.B], mesh.ExactVertices[f.C] })
            .ToArray();
        // The fan centre is the only point every child shares. The compiler's vertex order is not part of the contract.
        MapExactPoint[] shared = children.Length < 2 ? Array.Empty<MapExactPoint>()
            : children.Skip(1).Aggregate((IEnumerable<MapExactPoint>)children[0], (common, child) => common.Intersect(child)).ToArray();
        if (shared.Length != 1) throw new InvalidOperationException("parent is not a centroid fan");
        return children.Select(child =>
        {
            MapExactPoint[] edge = child.Where(p => p != shared[0]).OrderBy(p => p.X).ThenBy(p => p.Z).ToArray();
            if (edge.Length != 2) throw new InvalidOperationException("fan child does not use the centre once");
            return (edge[0], edge[1]);
        }).ToArray();
    }

    internal static MapExactValue ParentPlaneHeight(MapCompiledPatch mesh, MapCompiledFace face)
    {
        IReadOnlyList<MapExactPoint> corners = ParentCorners(mesh, face);
        MapExactPoint a = corners[0], b = corners[1], c = corners[2], centre = Centre(mesh, face);
        MapExactValue determinant = CrossXz(a, b, c);
        MapExactValue weightB = CrossXz(a, centre, c).Divide(determinant);
        MapExactValue weightC = CrossXz(a, b, centre).Divide(determinant);
        return a.Y.Add(b.Y.Subtract(a.Y).Multiply(weightB)).Add(c.Y.Subtract(a.Y).Multiply(weightC));
    }

    internal static MapExactValue CentreHeight(MapCompiledPatch mesh, MapCompiledFace face)
        => mesh.ExactVertices[face.A].Y.Add(mesh.ExactVertices[face.B].Y)
            .Add(mesh.ExactVertices[face.C].Y).Divide(new(3, 1));

    static MapExactPoint Centre(MapCompiledPatch mesh, MapCompiledFace face)
    {
        MapExactPoint a = mesh.ExactVertices[face.A], b = mesh.ExactVertices[face.B], c = mesh.ExactVertices[face.C];
        return new(a.X.Add(b.X).Add(c.X).Divide(new(3, 1)),
            a.Y.Add(b.Y).Add(c.Y).Divide(new(3, 1)), a.Z.Add(b.Z).Add(c.Z).Divide(new(3, 1)));
    }

    static IReadOnlyList<MapExactPoint> ParentCorners(MapCompiledPatch mesh, MapCompiledFace face)
    {
        MapExactPoint[] points = mesh.Faces.Where(f => f.Key.OwnerId == face.Key.OwnerId &&
                f.Key.Patch == face.Key.Patch && f.Key.Primitive == face.Key.Primitive &&
                f.Key.ParentTriangle == face.Key.ParentTriangle && f.Key.Side == face.Key.Side)
            .SelectMany(f => new[] { mesh.ExactVertices[f.A], mesh.ExactVertices[f.B], mesh.ExactVertices[f.C] })
            .Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToArray();
        // Removing collinear boundary points recovers the parent's original corner or mid-edge vertices.
        List<MapExactPoint> lower = HullHalf(points), upper = HullHalf(points.Reverse());
        if (lower.Count == 0 || upper.Count == 0) throw new InvalidOperationException("parent has no vertices");
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        if (lower.Count != 3) throw new InvalidOperationException("parent boundary is not a triangle");
        return lower;
    }

    static List<MapExactPoint> HullHalf(IEnumerable<MapExactPoint> points)
    {
        var hull = new List<MapExactPoint>();
        foreach (MapExactPoint point in points)
        {
            while (hull.Count >= 2 && CrossXz(hull[^2], hull[^1], point).Sign <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        return hull;
    }

    static MapExactValue CrossXz(MapExactPoint a, MapExactPoint b, MapExactPoint c)
        => b.X.Subtract(a.X).Multiply(c.Z.Subtract(a.Z))
            .Subtract(c.X.Subtract(a.X).Multiply(b.Z.Subtract(a.Z)));
}
