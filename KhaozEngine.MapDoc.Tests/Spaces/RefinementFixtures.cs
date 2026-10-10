using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class RefinementFixtures
{
    internal static MapExactXz X(long xn, long xd, long zn, long zd) => new(new(xn, xd), new(zn, zd));
    internal static readonly MapExactXz Origin00 = X(0, 1, 0, 1);
    internal static readonly MapExactXz One11 = X(1, 1, 1, 1);

    internal static IReadOnlyList<MapBoundFace> UnitSquareSwNe(string owner, int cm) => new[]
    {
        Face(owner, 0, cm, X(0, 1, 0, 1), X(1, 1, 0, 1), X(1, 1, 1, 1)),
        Face(owner, 1, cm, X(0, 1, 0, 1), X(1, 1, 1, 1), X(0, 1, 1, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> UnitSquareNwSe(string owner, int cm) => new[]
    {
        Face(owner, 0, cm, X(0, 1, 0, 1), X(1, 1, 0, 1), X(0, 1, 1, 1)),
        Face(owner, 1, cm, X(1, 1, 0, 1), X(1, 1, 1, 1), X(0, 1, 1, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> FanFromSouthMidpoint(string owner, int cm) => new[]
    {
        Face(owner, 0, cm, X(0, 1, 0, 1), X(1, 2, 0, 1), X(0, 1, 1, 1)),
        Face(owner, 1, cm, X(1, 2, 0, 1), X(1, 1, 1, 1), X(0, 1, 1, 1)),
        Face(owner, 2, cm, X(1, 2, 0, 1), X(1, 1, 0, 1), X(1, 1, 1, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> CollinearTriangle() => new[]
    {
        Face("lo", 0, 0, X(0, 1, 0, 1), X(1, 1, 0, 1), X(2, 1, 0, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> LowerUnitTriangle() => new[]
    {
        Face("lo", 0, 0, X(0, 1, 0, 1), X(1, 1, 0, 1), X(0, 1, 1, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> PrimeSliverUpper(long p) => new[]
    {
        Face("up", 0, 100, X(1, p, 0, 1), X(1, 1, 1, 1), X(1, p, 1, 1)),
    };

    internal static IReadOnlyList<MapBoundFace> UnitSquareGrid2(string owner, int cm) => Grid(owner, cm, 2);
    internal static IReadOnlyList<MapBoundFace> UnitSquareGrid16(string owner, int cm) => Grid(owner, cm, 16);

    static IReadOnlyList<MapBoundFace> Grid(string owner, int cm, int cells)
    {
        var faces = new List<MapBoundFace>(2 * cells * cells);
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                MapExactXz sw = X(i, cells, j, cells), se = X(i + 1, cells, j, cells);
                MapExactXz nw = X(i, cells, j + 1, cells), ne = X(i + 1, cells, j + 1, cells);
                faces.Add(Face(owner, faces.Count, cm, sw, se, ne));
                faces.Add(Face(owner, faces.Count, cm, sw, ne, nw));
            }
        return faces;
    }

    static MapBoundFace Face(string owner, int index, int cm, MapExactXz a, MapExactXz b, MapExactXz c)
    {
        MapExactValue height = new(cm, 100);
        // The listed index lives in Primitive so keys stay unique and in listed order beyond 256 faces.
        return new(new(owner, null, index, 0, 0, MapSide.Front),
            new(new(a.X, height, a.Z), new(b.X, height, b.Z), new(c.X, height, c.Z)));
    }

    internal static (IReadOnlyList<MapBoundFace> Lower, IReadOnlyList<MapBoundFace> Upper, MapExactXz Min, MapExactXz Max)
        LedgeBoundFaces(MapScopedSurfaces s)
    {
        MapSpaceFootprint footprint = s.RecordsIn(new("third", 1, 0)).OfType<MapSpaceFootprint>()
            .Single(f => f.Id == "ledge-cells");
        MapSurfaceRef lattice = s.Surfaces.Single(surface => surface.Id == footprint.Lattice.SurfaceId);
        MapExactXz[] rectangle = MapLatticeRanges.CellRect(lattice.Frame, footprint.Lattice, 31);
        MapBoundFaceSet lower = MapCommonRefinement.BoundFaces(s, footprint, footprint.Lower, rectangle[0], rectangle[1], new());
        MapBoundFaceSet upper = MapCommonRefinement.BoundFaces(s, footprint, footprint.Upper, rectangle[0], rectangle[1], new());
        if (lower.Status != MapRefinementStatus.Complete || upper.Status != MapRefinementStatus.Complete)
            throw new InvalidOperationException("ledge fixture bound enumeration failed: " + (lower.Detail ?? upper.Detail));
        return (lower.Faces, upper.Faces, rectangle[0], rectangle[1]);
    }

    internal static MapBoundFace TouchingFace() => new(new("half", new MapPatchKey("half", 1, 0), 0, 0, 0, MapSide.Front),
        new(new(new(32, 1), new(0, 1), new(0, 1)), new(new(65, 2), new(0, 1), new(0, 1)),
            new(new(32, 1), new(0, 1), new(1, 2))));
}
