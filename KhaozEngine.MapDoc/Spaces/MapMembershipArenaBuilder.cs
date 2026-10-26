using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Construction scope for eager columns. Only a complete product escapes its scope.</summary>
internal static class MapMembershipArenaBuilder
{
    [ThreadStatic] static MapMembershipGeometryArena? t_current;
    internal static MapMembershipGeometryArena Current => t_current ?? throw new InvalidOperationException("No membership arena builder.");
    internal static Scope Begin() => new(t_current ?? MapMembershipGeometryArena.Create());
    internal readonly struct Scope : IDisposable
    {
        readonly MapMembershipGeometryArena? _previous;
        readonly MapMembershipCoefficientStore.Scope _coefficients;
        internal MapMembershipGeometryArena Arena { get; }
        internal bool Owns => !ReferenceEquals(_previous, Arena);
        internal Scope(MapMembershipGeometryArena arena)
        { _previous = t_current; Arena = arena; t_current = arena; _coefficients = new(arena.Coefficients); }
        public void Dispose()
        { if (Owns) Arena.Freeze(); t_current = _previous; _coefficients.Dispose(); }
    }
    internal static int Polygon(IReadOnlyList<MapExactXz> vertices, bool cell)
    {
        var arena = Current;
        using var arithmetic = MapMembershipBuild.Kernel();
        int first = arena.Vertices.Count, affine = arena.Coefficients.Count;
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
            arena.Vertices.Append(a);
            _ = MapMembershipAffine.Edge(a, b);
            int dz = b.Z.CompareTo(a.Z);
            bool owns = dz < 0 || dz == 0 && b.X.CompareTo(a.X) > 0;
            int turn = MapWaterPolygons.Cross(MapWideXz.From(vertices[(i + vertices.Count - 1) % vertices.Count]),
                MapWideXz.From(a), MapWideXz.From(b)).Sign;
            arena.Corners.Append((byte)(turn + 1 | (owns ? 4 : 0)));
        }
        var bounds = new MapExactRect(vertices.Min(v => v.X), vertices.Min(v => v.Z), vertices.Max(v => v.X), vertices.Max(v => v.Z));
        int result = arena.Polygons.Append(new(bounds, first, vertices.Count, affine, cell ? 1 : 0));
        if (MapMembershipBuild.Current is { } build) { build.Counts.Polygons++; build.Counts.PolygonCorners += vertices.Count; }
        return result;
    }
    internal static int Triangle(MapExactTriangle triangle)
    {
        var arena = Current;
        using var arithmetic = MapMembershipBuild.Kernel();
        int first = arena.Coefficients.Count;
        MapExactXz a = new(triangle.A.X, triangle.A.Z), b = new(triangle.B.X, triangle.B.Z), c = new(triangle.C.X, triangle.C.Z);
        _ = MapMembershipAffine.Edge(a, b); _ = MapMembershipAffine.Edge(b, c); _ = MapMembershipAffine.Edge(c, a);
        int orientation = MapSubdividedTriangle.CrossWide(triangle.A, triangle.B, triangle.C).Sign;
        _ = MapMembershipAffine.Plane(triangle);
        bool Owns(MapExactXz from, MapExactXz to)
        {
            if (orientation < 0) (from, to) = (to, from);
            int dz = to.Z.CompareTo(from.Z);
            return dz < 0 || dz == 0 && to.X.CompareTo(from.X) > 0;
        }
        int owns = (Owns(a, b) ? 1 : 0) | (Owns(b, c) ? 2 : 0) | (Owns(c, a) ? 4 : 0);
        int index = arena.Triangles.Append(new(triangle, first, orientation, owns, 0));
        if (MapMembershipBuild.Current is { } build) build.Counts.Triangles++;
        return index;
    }
}
