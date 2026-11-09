using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact polygon handle with the released cell and general-ring boundary conventions.</summary>
internal readonly struct MapMembershipPolygon
{
    readonly MapMembershipGeometryArena _arena;
    readonly int _index;
    internal MapMembershipPolygon(MapMembershipGeometryArena arena, int index) { _arena = arena; _index = index; }
    internal int Index => _index;
    internal MapExactRect Bounds => _arena.Polygons[_index].Bounds;
    internal MapMembershipPolygon(IReadOnlyList<MapExactXz> vertices, bool cell = false)
    {
        using var building = MapMembershipArenaBuilder.Begin();
        _arena = building.Arena; _index = MapMembershipArenaBuilder.Polygon(vertices, cell);
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact)
    {
        ref readonly var row = ref _arena.Polygons[_index];
        var bounds = row.Bounds;
        if (point.X.CompareTo(bounds.MinX) < 0 || point.X.CompareTo(bounds.MaxX) > 0 ||
            point.Z.CompareTo(bounds.MinZ) < 0 || point.Z.CompareTo(bounds.MaxZ) > 0) return false;
        if ((row.Flags & 1) != 0)
        {
            int sign = 0;
            for (int i = 0; i < row.Count; i++)
            {
                int side = exact.Sign(new(_arena.Coefficients, row.Affine + 4 * i), point);
                if (side == 0) { if (i is 1 or 2) return false; continue; }
                if (sign != 0 && sign != side) return false;
                sign = side;
            }
            return sign != 0;
        }
        int winding = 0;
        for (int i = 0; i < row.Count; i++)
        {
            var a = _arena.Vertices[row.Vertex + i]; var b = _arena.Vertices[row.Vertex + (i + 1) % row.Count];
            byte corner = _arena.Corners[row.Vertex + i];
            bool owns = (corner & 4) != 0;
            if (point == a)
            {
                bool previous = (_arena.Corners[row.Vertex + (i + row.Count - 1) % row.Count] & 4) != 0;
                int turn = (corner & 3) - 1;
                return turn > 0 ? previous && owns : turn < 0 ? previous || owns : owns;
            }
            int side = exact.Sign(new(_arena.Coefficients, row.Affine + 4 * i), point);
            if (side == 0 && point != b && Between(point.X, a.X, b.X) && Between(point.Z, a.Z, b.Z)) return owns;
            if (a.Z.CompareTo(point.Z) <= 0)
            {
                if (b.Z.CompareTo(point.Z) > 0 && side > 0) winding++;
            }
            else if (b.Z.CompareTo(point.Z) <= 0 && side < 0) winding--;
        }
        return winding != 0;
    }
    internal static bool Between(MapExactValue value, MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0
        ? value.CompareTo(a) >= 0 && value.CompareTo(b) <= 0 : value.CompareTo(b) >= 0 && value.CompareTo(a) <= 0;
}

/// <summary>Compiled triangle handle with fixed-limb fallback and no mutable query storage.</summary>
internal readonly struct MapMembershipTriangle
{
    readonly MapMembershipGeometryArena _arena;
    readonly int _index;
    internal MapMembershipTriangle(MapMembershipGeometryArena arena, int index) { _arena = arena; _index = index; }
    internal MapExactTriangle Triangle => _arena.Triangles[_index].Triangle;
    MapMembershipAffine HeightPlane => new(_arena.Coefficients, _arena.Triangles[_index].Affine + 12);
    internal int Direction => HeightPlane.Direction;
    internal MapMembershipTriangle(MapExactTriangle triangle)
    {
        using var building = MapMembershipArenaBuilder.Begin();
        _arena = building.Arena; _index = MapMembershipArenaBuilder.Triangle(triangle);
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact, bool halfOpen = false)
    {
        ref readonly var row = ref _arena.Triangles[_index];
        for (int i = 0; i < 3; i++)
        {
            int sign = exact.Sign(new(_arena.Coefficients, row.Affine + 4 * i), point);
            if (sign * row.Orientation < 0 || sign == 0 && halfOpen && (row.Owns & (1 << i)) == 0) return false;
        }
        return row.Orientation != 0;
    }
    internal double HeightDouble(MapExactXz point, MapMembershipExact exact) => exact.ValueDouble(HeightPlane, point);
    internal MapExactValue? Height(MapExactXz point, MapMembershipExact exact)
    {
        if (!Contains(point, exact)) return null;
        if (!exact.TryValue(HeightPlane, point, out var height)) { exact.NotRepresentable = true; return null; }
        return height;
    }
}
