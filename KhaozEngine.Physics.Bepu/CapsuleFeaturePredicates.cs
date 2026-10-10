using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

internal enum TrianglePointLocation : byte
{
    Unresolved,
    Outside,
    Interior,
    Edge,
    Vertex,
    Degenerate,
}

/// <summary>Exact relations of supplied represented finite geometry. These predicates do not
/// project uncertain witnesses, authenticate installed shapes or establish support eligibility.</summary>
internal static class CapsuleFeaturePredicates
{
    internal static TrianglePointLocation ClassifyTrianglePoint(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(point)) return TrianglePointLocation.Unresolved;
        GeometrySign xy = Orient(a, b, c, 0, 1), yz = Orient(a, b, c, 1, 2), zx = Orient(a, b, c, 2, 0);
        if (xy == GeometrySign.Unresolved || yz == GeometrySign.Unresolved || zx == GeometrySign.Unresolved)
            return TrianglePointLocation.Unresolved;
        if (xy == GeometrySign.Zero && yz == GeometrySign.Zero && zx == GeometrySign.Zero)
            return TrianglePointLocation.Degenerate;
        GeometrySign plane = BoundedGeometryArithmetic.Orient3D(a, b, c, point);
        if (plane == GeometrySign.Unresolved) return TrianglePointLocation.Unresolved;
        if (plane != GeometrySign.Zero) return TrianglePointLocation.Outside;

        // A nonzero projected area makes this projection one-to-one on the triangle's plane.
        int first = xy != GeometrySign.Zero ? 0 : yz != GeometrySign.Zero ? 1 : 2;
        int second = (first + 1) % 3;
        GeometrySign winding = first == 0 ? xy : first == 1 ? yz : zx;
        GeometrySign ab = Orient(a, b, point, first, second);
        GeometrySign bc = Orient(b, c, point, first, second);
        GeometrySign ca = Orient(c, a, point, first, second);
        if (ab == GeometrySign.Unresolved || bc == GeometrySign.Unresolved || ca == GeometrySign.Unresolved)
            return TrianglePointLocation.Unresolved;
        if (Opposes(ab, winding) || Opposes(bc, winding) || Opposes(ca, winding)) return TrianglePointLocation.Outside;
        int zeroCount = (ab == GeometrySign.Zero ? 1 : 0) + (bc == GeometrySign.Zero ? 1 : 0) +
            (ca == GeometrySign.Zero ? 1 : 0);
        return zeroCount switch
        {
            0 => TrianglePointLocation.Interior,
            1 => TrianglePointLocation.Edge,
            2 => TrianglePointLocation.Vertex,
            _ => TrianglePointLocation.Unresolved,
        };
    }

    static GeometrySign Orient(Vector3 a, Vector3 b, Vector3 c, int first, int second) =>
        BoundedGeometryArithmetic.Orient2D(Component(a, first), Component(a, second), Component(b, first),
            Component(b, second), Component(c, first), Component(c, second));
    static float Component(Vector3 point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
    static bool Opposes(GeometrySign edge, GeometrySign winding) => edge != GeometrySign.Zero && edge != winding;
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
