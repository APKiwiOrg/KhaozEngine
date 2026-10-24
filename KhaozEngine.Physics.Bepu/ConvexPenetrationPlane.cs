using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The single upper verifier, bound to the original source, request and exact feet.
/// Its attained support points and direction certify the two closed normal cones.</summary>
internal readonly struct ConvexPenetrationPlane
{
    internal ConvexQueryRequest Request { get; }
    internal ConvexPlanarSource Source { get; }
    internal P OriginalFeet { get; }
    internal P Direction { get; }
    internal P Query { get; }
    internal P Target { get; }
    internal int Vertex { get; }
    internal Vector3 Translation { get; }
    internal GeometryInterval Length { get; }
    internal double AdditionError { get; }

    ConvexPenetrationPlane(ConvexQueryRequest request, ConvexPlanarSource source, P feet,
        P n, P q, P t, int vertex, Vector3 translation, GeometryInterval length, double additionError)
    {
        Request = request; Source = source; OriginalFeet = feet; Direction = n; Query = q; Target = t;
        Vertex = vertex; Translation = translation; Length = length; AdditionError = additionError;
    }

    internal bool Matches(in ConvexPenetrationOrigin pair) => pair.IsAlive && Source.IsValid &&
        ReferenceEquals(Source.Token, pair.Source.Token) && Request == pair.Request && OriginalFeet == pair.Feet;

    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        P proposal, ref ConvexQueryWork work, out ConvexPenetrationPlane plane)
    {
        plane = default;
        return ConvexPenetrationProposal.TryCreate(request, source, feet, proposal, ref work, out var bound) &&
            TryVerify(request, source, feet, bound, ref work, out plane);
    }

    internal static bool TryVerify(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ConvexPenetrationProposal proposal, ref ConvexQueryWork work, out ConvexPenetrationPlane plane)
    {
        plane = default;
        if (!source.IsOriginal || !source.IsValid || !proposal.Matches(request, source, feet) ||
            !work.Cell() || !work.Feature()) return false;
        P n = proposal.Direction, t = proposal.Target;
        Q squared = proposal.Squared; int vertex = proposal.Vertex;
        GeometryInterval support = proposal.Support;
        if (support.Upper <= 0) return false;
        P q = ConvexPenetrationSupport.Exact(request.Shape, n);
        if (!work.Feature() || !ConvexPlanarFeatures.Member(request.Shape, q) ||
            !ConvexPlanarFeatures.NormalCone(request.Shape, q, n)) return false;
        GeometryInterval scale = support.Divide(squared.Bounds);
        if (!scale.IsResolved) return false;
        P ideal = n * Q.From(-scale.Upper);
        float Away(GeometryInterval a, Q component) => component.Sign == 0 ? 0 :
            component.Sign > 0 ? Down(a.Lower) : Up(a.Upper);
        Vector3 translation = new(Away(ideal.X.Bounds, n.X), Away(ideal.Y.Bounds, n.Y), Away(ideal.Z.Bounds, n.Z));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!work.Feature() || !Finite(translation)) return false;
            Vector3 destination = new(Mid(feet.X.Bounds) + translation.X, Mid(feet.Y.Bounds) + translation.Y, Mid(feet.Z.Bounds) + translation.Z);
            if (!Finite(destination)) return false;
            P exact = P.From(translation), operational = P.From(destination) - feet;
            if ((-P.Dot(n, exact)).Bounds.Lower >= support.Upper && (-P.Dot(n, operational)).Bounds.Lower >= support.Upper)
            {
                P residual = operational - exact;
                plane = new(request, source, feet, n, q, t, vertex, translation,
                    Q.RootBounds(P.Dot(exact, exact)), Q.RootBounds(P.Dot(residual, residual)).Upper);
                return plane.Length.IsResolved;
            }
            float Next(float value, Q component) => component.Sign == 0 ? value :
                component.Sign > 0 ? MathF.BitDecrement(value) : MathF.BitIncrement(value);
            destination = new(Next(destination.X, n.X), Next(destination.Y, n.Y), Next(destination.Z, n.Z));
            P step = P.From(destination) - feet;
            translation = new(Away(step.X.Bounds, n.X), Away(step.Y.Bounds, n.Y), Away(step.Z.Bounds, n.Z));
        }
        return false;
    }

    internal bool TryFeature(ref ConvexQueryWork work, out ConvexTargetFeature feature, out int id)
    {
        var source = Source; var feet = OriginalFeet; var n = Direction; int vertex = Vertex;
        Q minimum = P.Dot(n, Target);
        feature = ConvexTargetFeature.Vertex; id = source.FaceCount + source.EdgeCount + vertex;
        for (int f = 0; f < source.FaceCount; f++)
        {
            bool all = true;
            for (int i = 0; i < source.InteriorFaceSize(f); i++)
            {
                if (!work.Feature()) return false;
                if ((P.Dot(n, source.Vertex(source.FaceVertex(f, i), feet)) - minimum).Sign != 0) { all = false; break; }
            }
            if (all) { feature = ConvexTargetFeature.Face; id = f; return true; }
        }
        for (int e = 0; e < source.EdgeCount; e++)
        {
            if (!work.Feature()) return false;
            var edge = source.Edge(e);
            if ((P.Dot(n, source.Vertex(edge.A, feet)) - minimum).Sign != 0 ||
                (P.Dot(n, source.Vertex(edge.B, feet)) - minimum).Sign != 0) continue;
            feature = ConvexTargetFeature.Edge; id = source.FaceCount + e; return true;
        }
        return true;
    }

    static float Mid(GeometryInterval a) => (float)(a.Lower * 0.5 + a.Upper * 0.5);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static float Down(double x) { float r = (float)x; return r > x ? MathF.BitDecrement(r) : r; }
    static float Up(double x) { float r = (float)x; return r < x ? MathF.BitIncrement(r) : r; }
}
