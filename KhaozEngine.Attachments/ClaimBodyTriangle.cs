using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal readonly record struct BodyTriangleProjection(double DistanceSquared, double HeightMetres, double W1, double W2);

internal readonly struct ClaimBodyTriangle
{
    readonly ClaimPoint a;
    readonly ClaimPoint b;
    readonly ClaimPoint c;
    readonly ClaimPoint ab;
    readonly ClaimPoint bc;
    readonly ClaimPoint ca;
    readonly ClaimPoint normal;
    readonly double normalSquared;
    readonly double normalLength;

    internal ClaimBodyTriangle(int index, int region, ClaimPoint a, ClaimPoint b, ClaimPoint c)
    {
        Index = index;
        Region = region;
        this.a = a;
        this.b = b;
        this.c = c;
        ab = b - a;
        bc = c - b;
        ca = a - c;
        normal = ClaimPoint.Cross(ab, c - a);
        normalSquared = normal.LengthSquared;
        normalLength = Math.Sqrt(normalSquared);
    }

    internal int Index { get; }
    internal int Region { get; }
    internal bool HasNormal => normalLength > 0;
    internal Vector3 UnitNormal => new((float)(normal.X / normalLength), (float)(normal.Y / normalLength),
        (float)(normal.Z / normalLength));
    internal ClaimPoint Min => ClaimPoint.Min(a, ClaimPoint.Min(b, c));
    internal ClaimPoint Max => ClaimPoint.Max(a, ClaimPoint.Max(b, c));

    // The longest edge squared over the doubled area bounds the inverse of the triangle's smallest altitude
    // relative to that edge. Skinny triangles need more room for roundoff when rejecting a bounding box.
    internal double ShapeCondition => Math.Max(ab.LengthSquared, Math.Max(bc.LengthSquared, ca.LengthSquared)) / normalLength;

    internal BodyTriangleProjection Project(ClaimPoint vertex)
    {
        ClaimPoint fromA = vertex - a;
        ClaimPoint fromB = vertex - b;
        ClaimPoint fromC = vertex - c;
        double height = ClaimPoint.Dot(fromA, normal) / normalLength;

        // These side tests see only the projection into the triangle's plane. Each side value is the
        // opposite corner's barycentric numerator. Outside the triangle the closest point is on an edge.
        double sideAB = Side(ab, fromA);
        double sideBC = Side(bc, fromB);
        double sideCA = Side(ca, fromC);
        if (sideAB >= 0 && sideBC >= 0 && sideCA >= 0)
            return new(height * height, height, sideCA / normalSquared, sideAB / normalSquared);

        (double distanceAB, double tAB) = Segment(ab, fromA, fromB);
        (double distanceBC, double tBC) = Segment(bc, fromB, fromC);
        (double distanceCA, double tCA) = Segment(ca, fromC, fromA);
        if (distanceAB <= distanceBC && distanceAB <= distanceCA)
            return new(distanceAB, height, tAB, 0);
        if (distanceBC <= distanceCA)
            return new(distanceBC, height, 1 - tBC, tBC);
        return new(distanceCA, height, 0, 1 - tCA);
    }

    double Side(ClaimPoint edge, ClaimPoint offset) => ClaimPoint.Dot(ClaimPoint.Cross(edge, offset), normal);

    static (double DistanceSquared, double T) Segment(ClaimPoint edge, ClaimPoint fromStart, ClaimPoint fromEnd)
    {
        double along = ClaimPoint.Dot(edge, fromStart);
        if (along <= 0)
            return (fromStart.LengthSquared, 0);

        double lengthSquared = edge.LengthSquared;
        if (along >= lengthSquared)
            return (fromEnd.LengthSquared, 1);

        double t = along / lengthSquared;
        ClaimPoint offset = fromStart - edge * t;
        return (offset.LengthSquared, t);
    }
}
