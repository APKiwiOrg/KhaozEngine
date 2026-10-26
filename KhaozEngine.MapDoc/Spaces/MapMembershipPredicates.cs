using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Immutable exact edges, with the released cell or OA7 polygon boundary convention.</summary>
internal sealed class MapMembershipPolygon
{
    internal long InventoryBytes => checked(128L + 53L * _vertices.Length + (_coefficientOwner?.InventoryBytes ?? 0));
    readonly MapMembershipCoefficientStore? _coefficientOwner;
    readonly MapExactXz[] _vertices;
    readonly MapMembershipAffine[] _edges;
    readonly bool[] _owns;
    readonly int[] _turns;
    readonly bool _cell;
    readonly MapExactValue _minX, _maxX, _minZ, _maxZ;
    internal MapExactRect Bounds => new(_minX, _minZ, _maxX, _maxZ);
    internal MapMembershipPolygon(IReadOnlyList<MapExactXz> vertices, bool cell = false)
    {
        using var encoding = MapMembershipCoefficientStore.Begin();
        _coefficientOwner = encoding.Owns ? encoding.Store : null;
        MapMembershipBuild.Inventory(checked(128L + 53L * vertices.Count));
        if (MapMembershipBuild.Current is { } build)
        { build.Counts.Polygons++; build.Counts.PolygonCorners += vertices.Count; }
        using var arithmetic = MapMembershipBuild.Kernel();
        _vertices = vertices.ToArray();
        _cell = cell;
        _edges = new MapMembershipAffine[vertices.Count];
        _owns = new bool[vertices.Count];
        _turns = new int[vertices.Count];
        _minX = vertices.Min(v => v.X); _maxX = vertices.Max(v => v.X);
        _minZ = vertices.Min(v => v.Z); _maxZ = vertices.Max(v => v.Z);
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
            _edges[i] = MapMembershipAffine.Edge(a, b);
            int dz = b.Z.CompareTo(a.Z);
            _owns[i] = dz < 0 || dz == 0 && b.X.CompareTo(a.X) > 0;
            _turns[i] = MapWaterPolygons.Cross(MapWideXz.From(vertices[(i + vertices.Count - 1) % vertices.Count]),
                MapWideXz.From(a), MapWideXz.From(b)).Sign;
        }
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact)
    {
        if (point.X.CompareTo(_minX) < 0 || point.X.CompareTo(_maxX) > 0 ||
            point.Z.CompareTo(_minZ) < 0 || point.Z.CompareTo(_maxZ) > 0) return false;
        if (_cell)
        {
            int sign = 0;
            for (int i = 0; i < _edges.Length; i++)
            {
                int side = exact.Sign(_edges[i], point);
                if (side == 0) { if (i is 1 or 2) return false; continue; }
                if (sign != 0 && sign != side) return false;
                sign = side;
            }
            return sign != 0;
        }
        int winding = 0;
        for (int i = 0; i < _edges.Length; i++)
        {
            var a = _vertices[i]; var b = _vertices[(i + 1) % _edges.Length];
            if (point == a)
            {
                bool previous = _owns[(i + _edges.Length - 1) % _edges.Length];
                return _turns[i] > 0 ? previous && _owns[i] : _turns[i] < 0 ? previous || _owns[i] : _owns[i];
            }
            int side = exact.Sign(_edges[i], point);
            if (side == 0 && point != b && Between(point.X, a.X, b.X) && Between(point.Z, a.Z, b.Z)) return _owns[i];
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

/// <summary>A compiled triangle whose filter and fixed-limb fallback agree with released wide predicates.</summary>
internal sealed class MapMembershipTriangle
{
    internal long InventoryBytes => checked(251L + (_coefficientOwner?.InventoryBytes ?? 0));
    readonly MapMembershipCoefficientStore? _coefficientOwner;
    readonly MapMembershipAffine[] _edges;
    readonly bool[] _owns;
    readonly int _orientation;
    readonly MapMembershipAffine _height;
    internal MapExactTriangle Triangle { get; }
    internal int Direction => _height.Direction;
    internal MapMembershipTriangle(MapExactTriangle triangle)
    {
        using var encoding = MapMembershipCoefficientStore.Begin();
        _coefficientOwner = encoding.Owns ? encoding.Store : null;
        MapMembershipBuild.Inventory(251);
        if (MapMembershipBuild.Current is { } build) build.Counts.Triangles++;
        Triangle = triangle;
        MapExactXz a = new(triangle.A.X, triangle.A.Z), b = new(triangle.B.X, triangle.B.Z), c = new(triangle.C.X, triangle.C.Z);
        _edges = [MapMembershipAffine.Edge(a, b), MapMembershipAffine.Edge(b, c), MapMembershipAffine.Edge(c, a)];
        _orientation = MapSubdividedTriangle.CrossWide(triangle.A, triangle.B, triangle.C).Sign;
        _height = MapMembershipAffine.Plane(triangle);
        bool Owns(MapExactXz from, MapExactXz to)
        {
            if (_orientation < 0) (from, to) = (to, from);
            int dz = to.Z.CompareTo(from.Z);
            return dz < 0 || dz == 0 && to.X.CompareTo(from.X) > 0;
        }
        _owns = [Owns(a, b), Owns(b, c), Owns(c, a)];
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact, bool halfOpen = false)
    {
        for (int i = 0; i < 3; i++)
        {
            int sign = exact.Sign(_edges[i], point);
            if (sign * _orientation < 0 || sign == 0 && halfOpen && !_owns[i]) return false;
        }
        return _orientation != 0;
    }
    internal double HeightDouble(MapExactXz point, MapMembershipExact exact) => exact.ValueDouble(_height, point);

    internal MapExactValue? Height(MapExactXz point, MapMembershipExact exact)
    {
        if (!Contains(point, exact)) return null;
        if (!exact.TryValue(_height, point, out var height)) { exact.NotRepresentable = true; return null; }
        return height;
    }
}
