using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Exact planar height-band intersection with the closed doubled-radius capsule projection.</summary>
internal sealed class ContactTerrainHeightBand(Vector3 from, Vector3 to, double radius)
{
    readonly DescentPoint _from = new(from.X, from.Z), _to = new(to.X, to.Z);
    readonly DescentNumber _radiusSquared = new DescentNumber(radius) * new DescentNumber(radius);
    readonly record struct Vertex(DescentPoint Point, DescentNumber Excess);

    internal bool Allows(ReadOnlySpan<PhysicsEvidencePoint> face, IReadOnlyList<DescentPoint> polygon, double lower, double upper)
    {
        var heights = new DescentNumber[polygon.Count];
        for (int i = 0; i < polygon.Count; i++) heights[i] = DescentGeometry.Height(face, polygon[i]);
        return !Violates(polygon, heights, new(upper), true) && !Violates(polygon, heights, new(lower), false);
    }

    bool Violates(IReadOnlyList<DescentPoint> polygon, DescentNumber[] heights, DescentNumber limit, bool above)
    {
        var clipped = new List<Vertex>();
        bool positive = false;
        var previous = At(polygon.Count - 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            var current = At(i);
            positive |= current.Excess.Sign > 0;
            if ((previous.Excess.Sign < 0) != (current.Excess.Sign < 0))
                clipped.Add(new(DescentPoint.Mix(previous.Point, current.Point,
                    previous.Excess / (previous.Excess - current.Excess)), default));
            if (current.Excess.Sign >= 0) clipped.Add(current);
            previous = current;
        }
        if (!positive) return false;
        // Clip the violating half-plane rationally, then intersect its convex polygon with the exact capsule.
        // Squared closest distances include the circular caps without constructing irrational arc vertices.
        if (Contains(clipped, _from) || Contains(clipped, _to)) return true;
        for (int i = 0; i < clipped.Count; i++)
        {
            var a = clipped[i]; var b = clipped[(i + 1) % clipped.Count];
            if (Crosses(_from, _to, a.Point, b.Point) ||
                Reaches(a.Point, Closest(_from, _to, a.Point, out _), a.Excess) ||
                AtEndpoint(a, b, _from) || AtEndpoint(a, b, _to)) return true;
        }
        return false;
        Vertex At(int i) => new(polygon[i], above ? heights[i] - limit : limit - heights[i]);
    }

    bool AtEndpoint(Vertex a, Vertex b, DescentPoint end)
    {
        var point = Closest(a.Point, b.Point, end, out var t);
        return Reaches(point, end, a.Excess + (b.Excess - a.Excess) * t);
    }

    bool Reaches(DescentPoint ground, DescentPoint axis, DescentNumber excess)
    {
        var dx = ground.X - axis.X; var dz = ground.Z - axis.Z;
        int order = (dx * dx + dz * dz).CompareTo(_radiusSquared);
        // Strict overlap reaches a positive-height neighbourhood. A tangent witness must itself violate.
        return order < 0 || order == 0 && excess.Sign > 0;
    }

    static DescentPoint Closest(DescentPoint a, DescentPoint b, DescentPoint p, out DescentNumber t)
    {
        var dx = b.X - a.X; var dz = b.Z - a.Z;
        var length = dx * dx + dz * dz;
        t = length.Sign == 0 ? default : ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / length;
        if (t.Sign < 0) t = default;
        else if (t > new DescentNumber(1)) t = new(1);
        return DescentPoint.Mix(a, b, t);
    }

    static DescentNumber Cross(DescentPoint a, DescentPoint b, DescentPoint p) =>
        (b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X);

    static bool Crosses(DescentPoint a, DescentPoint b, DescentPoint p, DescentPoint q)
    {
        var u = Cross(a, b, p); var v = Cross(a, b, q);
        var s = Cross(p, q, a); var t = Cross(p, q, b);
        return (u - v).Sign != 0 && u.Sign * v.Sign <= 0 && s.Sign * t.Sign <= 0;
    }

    static bool Contains(List<Vertex> polygon, DescentPoint point)
    {
        int side = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            int sign = Cross(polygon[i].Point, polygon[(i + 1) % polygon.Count].Point, point).Sign;
            if (sign == 0) continue;
            if (side != 0 && side != sign) return false;
            side = sign;
        }
        return side != 0;
    }
}
