using System;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The released MapDoc entry points over the shared scalar-double convex kernel.</summary>
internal static class MapConvexQueries
{
    internal static double Distance(ReadOnlySpan<MapDouble3> a, ReadOnlySpan<MapDouble3> b) => ShapeQueryMath.Distance(a, b);
    internal static bool Raycast(ReadOnlySpan<MapDouble3> points, MapDouble3 start, MapDouble3 direction, double maxT,
        out double t, out MapDouble3 normal) => ShapeQueryMath.Raycast(points, start, direction, maxT, out t, out normal);
    internal static MapDouble3[] ClipToSlab(ReadOnlySpan<MapDouble3> points, double minY, double maxY) =>
        ShapeQueryMath.ClipToSlab(points, minY, maxY);
    internal static double Dot(MapDouble3 a, MapDouble3 b) => ShapeQueryMath.Dot(a, b);
    internal static MapDouble3 ClosestOnTriangle(MapDouble3 point, MapDouble3 a, MapDouble3 b, MapDouble3 c) =>
        ShapeQueryMath.ClosestOnTriangle(point, a, b, c);
}
