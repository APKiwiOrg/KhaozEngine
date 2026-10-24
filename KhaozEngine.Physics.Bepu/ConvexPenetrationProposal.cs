using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One direction's original-target minimum and complete shell support enclosure.
/// The plane verifier and inner-hull refinement reuse the same evaluated support data.</summary>
internal readonly struct ConvexPenetrationProposal
{
    readonly ConvexQueryRequest _request;
    readonly ConvexPlanarSource _source;
    readonly P _feet;
    internal P Direction { get; }
    internal P Target { get; }
    internal int Vertex { get; }
    internal Q Squared { get; }
    internal GeometryInterval Support { get; }
    internal GeometryInterval Depth { get; }

    ConvexPenetrationProposal(ConvexQueryRequest request, ConvexPlanarSource source, P feet,
        P direction, P target, int vertex, Q squared, GeometryInterval support, GeometryInterval depth)
    {
        _request = request; _source = source; _feet = feet;
        Direction = direction; Target = target; Vertex = vertex;
        Squared = squared; Support = support; Depth = depth;
    }

    internal bool Matches(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet) =>
        _source.IsValid && ReferenceEquals(_source.Token, source.Token) && _request == request && _feet == feet;

    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        P direction, ref ConvexQueryWork work, out ConvexPenetrationProposal proposal)
    {
        proposal = default;
        if (!source.IsOriginal || !source.IsValid || !work.Cell() || !work.Feature()) return false;
        var bounds = direction.Bounds;
        if (!bounds.IsResolved) return false;
        double extent = Math.Max(Math.Max(Abs(bounds.X), Abs(bounds.Y)), Abs(bounds.Z));
        if (!(extent > 0) || !double.IsFinite(extent)) return false;
        float Component(GeometryInterval a) => (float)((a.Lower * 0.5 + a.Upper * 0.5) / extent);
        P n;
        if (direction.X.Rational && direction.Y.Rational && direction.Z.Rational)
        {
            Q divisor = direction.X.Sign < 0 ? -direction.X : direction.X;
            Q y = direction.Y.Sign < 0 ? -direction.Y : direction.Y, z = direction.Z.Sign < 0 ? -direction.Z : direction.Z;
            if ((y - divisor).Sign > 0) divisor = y;
            if ((z - divisor).Sign > 0) divisor = z;
            n = direction * (Q.From(1) / divisor);
        }
        else n = P.From(new Vector3(Component(bounds.X), Component(bounds.Y), Component(bounds.Z)));
        Q squared = P.Dot(n, n);
        if (squared.Sign <= 0) return false;
        Q minimum = default; int vertex = 0;
        for (int i = 0; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            Q projection = P.Dot(n, source.Vertex(i, feet));
            if (i == 0 || (projection - minimum).Sign < 0) { minimum = projection; vertex = i; }
        }
        GeometryInterval length = Q.RootBounds(squared);
        GeometryInterval radial = Q.RootBounds(n.X * n.X + n.Z * n.Z).Multiply(I(request.Shape.Radius));
        GeometryInterval apex = (n.Y * Q.From(request.Shape.LowerY)).Bounds;
        GeometryInterval ring = (n.Y * Q.From(request.Shape.RingY)).Bounds.Add(radial);
        GeometryInterval head = (n.Y * Q.From(request.Shape.UpperY)).Bounds.Add(radial);
        GeometryInterval support = GeometryInterval.Enclose(Math.Max(apex.Lower, Math.Max(ring.Lower, head.Lower)),
            Math.Max(apex.Upper, Math.Max(ring.Upper, head.Upper)))
            .Add(length.Multiply(I(request.Shape.Margin))).Subtract(minimum.Bounds);
        GeometryInterval depth = support.Divide(length);
        if (!support.IsResolved || !depth.IsResolved) return false;
        proposal = new(request, source, feet, n, source.Vertex(vertex, feet), vertex, squared, support, depth);
        return true;
    }

    static GeometryInterval I(double x) => GeometryInterval.Exact(x);
    static double Abs(GeometryInterval a) => Math.Max(Math.Abs(a.Lower), Math.Abs(a.Upper));
}
