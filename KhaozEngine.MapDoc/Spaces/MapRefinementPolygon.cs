using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact convex clipping and canonical polygon order for generated refinement geometry.</summary>
internal static class MapRefinementPolygon
{
    internal static MapExactValue Cross(MapExactXz a, MapExactXz b, MapExactXz p) =>
        b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z))
            .Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X)));

    internal static MapExactValue SignedArea(IReadOnlyList<MapExactXz> polygon)
    {
        MapExactValue twice = default;
        // An origin-relative fan avoids large absolute-coordinate products in translated worlds.
        for (int i = 1; i + 1 < polygon.Count; i++)
            twice = twice.Add(Cross(polygon[0], polygon[i], polygon[i + 1]));
        return twice.Divide(new(2, 1));
    }

    internal static MapExactValue Area(IReadOnlyList<MapExactXz> polygon)
    {
        MapExactValue area = SignedArea(polygon);
        return area.Sign < 0 ? area.Negate() : area;
    }

    internal static List<MapExactXz> Triangle(MapExactTriangle triangle)
    {
        var polygon = new List<MapExactXz>
        {
            new(triangle.A.X, triangle.A.Z), new(triangle.B.X, triangle.B.Z), new(triangle.C.X, triangle.C.Z),
        };
        if (SignedArea(polygon).Sign < 0) polygon.Reverse();
        return polygon;
    }

    internal static List<MapExactXz> Rectangle(List<MapExactXz> polygon, MapExactXz min, MapExactXz max)
    {
        polygon = Clip(polygon, p => p.X.Subtract(min.X));
        polygon = Clip(polygon, p => max.X.Subtract(p.X));
        polygon = Clip(polygon, p => p.Z.Subtract(min.Z));
        return Clip(polygon, p => max.Z.Subtract(p.Z));
    }

    internal static List<MapExactXz> Intersect(IReadOnlyList<MapExactXz> lower, IReadOnlyList<MapExactXz> upper)
    {
        var polygon = lower.ToList();
        for (int i = 0; i < upper.Count && polygon.Count != 0; i++)
        {
            MapExactXz a = upper[i], b = upper[(i + 1) % upper.Count];
            polygon = Clip(polygon, p => Cross(a, b, p));
        }
        return polygon;
    }

    static List<MapExactXz> Clip(IReadOnlyList<MapExactXz> polygon, Func<MapExactXz, MapExactValue> distance)
    {
        var result = new List<MapExactXz>();
        if (polygon.Count == 0) return result;
        MapExactXz previous = polygon[^1];
        MapExactValue cp = distance(previous);
        foreach (MapExactXz current in polygon)
        {
            MapExactValue cq = distance(current);
            if ((cp.Sign < 0) != (cq.Sign < 0))
            {
                MapExactValue t = cp.Divide(cp.Subtract(cq));
                Add(new(previous.X.Add(current.X.Subtract(previous.X).Multiply(t)),
                    previous.Z.Add(current.Z.Subtract(previous.Z).Multiply(t))));
            }
            if (cq.Sign >= 0) Add(current);
            previous = current;
            cp = cq;
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result;

        void Add(MapExactXz point)
        {
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }
    }

    internal static IReadOnlyList<MapExactXz> Normalize(List<MapExactXz> polygon)
    {
        if (SignedArea(polygon).Sign < 0) polygon.Reverse();
        int first = 0;
        for (int i = 1; i < polygon.Count; i++)
            if (polygon[i].CompareTo(polygon[first]) < 0) first = i;
        var ordered = new MapExactXz[polygon.Count];
        for (int i = 0; i < ordered.Length; i++) ordered[i] = polygon[(first + i) % polygon.Count];
        return Array.AsReadOnly(ordered);
    }

    internal readonly record struct Bounds(MapExactValue MinX, MapExactValue MinZ, MapExactValue MaxX, MapExactValue MaxZ)
    {
        internal static Bounds Of(IReadOnlyList<MapExactXz> polygon) =>
            new(polygon.Min(p => p.X), polygon.Min(p => p.Z), polygon.Max(p => p.X), polygon.Max(p => p.Z));
        internal bool Overlaps(Bounds other) => MinX.CompareTo(other.MaxX) < 0 && MaxX.CompareTo(other.MinX) > 0 &&
            MinZ.CompareTo(other.MaxZ) < 0 && MaxZ.CompareTo(other.MinZ) > 0;
    }
}
