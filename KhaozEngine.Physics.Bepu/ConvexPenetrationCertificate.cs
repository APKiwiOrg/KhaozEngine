using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>An original-member depth bracket with an inball lower proof and one upper plane.
/// The plane's original support witnesses certify its normal without another distance query.</summary>
internal readonly struct ConvexPenetrationCertificate
{
    internal ConvexPenetrationOrigin Initial { get; }
    internal ConvexPenetrationWitness Attained { get; }
    internal P Query => Attained.Query;
    internal P Target => Attained.Target;
    internal P Direction => Attained.Direction;
    internal float LowerDepth { get; }
    internal float UpperDepth { get; }
    internal Vector3 Translation { get; }
    internal ConvexLeafEvidence Contact { get; }
    internal GeometryInterval GlobalLowerDepthBounds { get; }
    internal bool IsAlive => Initial.IsAlive && Attained.IsAlive && Contact.IsComplete;

    ConvexPenetrationCertificate(ConvexPenetrationOrigin initial, ConvexPenetrationWitness attained,
        float lower, float upper, Vector3 translation, ConvexLeafEvidence contact, GeometryInterval lowerProof)
    {
        Initial = initial; Attained = attained; LowerDepth = lower; UpperDepth = upper;
        Translation = translation; Contact = contact; GlobalLowerDepthBounds = lowerProof;
    }

    internal static bool TryCreate(ConvexPenetrationOrigin initial, ConvexPenetrationPlane plane,
        GeometryInterval lowerProof, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexPenetrationCertificate result)
    {
        result = default;
        if (!initial.IsAlive || !initial.IsOwnedBy(scratch) || initial.State != ConvexInitialState.Overlapping ||
            !plane.Matches(initial) || !lowerProof.IsResolved || lowerProof.Lower <= 0 ||
            plane.Direction == default || !plane.Length.IsResolved) return false;
        float lo = Down(lowerProof.Lower), hi = Up(plane.Length.Upper);
        if (!float.IsFinite(lo) || !float.IsFinite(hi) || hi < lo || (double)hi - lo + plane.AdditionError > 0.001f) return false;
        if (!work.Cell() || !work.Feature() || !plane.TryFeature(ref work, out var feature, out int id)) return false;
        var pair = initial; var request = pair.Request;
        var attained = new ConvexPenetrationWitness(pair, plane);
        GeometryInterval length = Q.RootBounds(P.Dot(plane.Direction, plane.Direction));
        if (!length.IsResolved || length.Lower <= 0) return false;
        var n = plane.Direction.Bounds;
        GeometryVector normal = new(n.X.Divide(length), n.Y.Divide(length), n.Z.Divide(length));
        GeometryVector query = plane.Query.Bounds;
        GeometryInterval margin = GeometryInterval.Exact(request.Shape.Margin);
        GeometryVector grown = new(query.X.Add(normal.X.Multiply(margin)), query.Y.Add(normal.Y.Multiply(margin)), query.Z.Add(normal.Z.Multiply(margin)));
        GeometryVector worldQuery = Add(grown, pair.Feet.Bounds), worldTarget = Add(plane.Target.Bounds, pair.Feet.Bounds);
        if (!work.Feature()) return false;
        if (!Encode(worldQuery, out Vector3 qw, out double qe) || !Encode(worldTarget, out Vector3 tw, out double te) ||
            !Encode(normal, out Vector3 encodedNormal, out double ne) || encodedNormal == Vector3.Zero) return false;
        double error = GeometryInterval.Exact((double)hi - lo).Add(GeometryInterval.Exact(qe)).Add(GeometryInterval.Exact(te))
            .Add(GeometryInterval.Exact(ne).Multiply(GeometryInterval.Exact(Math.Max(1, hi))))
            .Add(GeometryInterval.Exact(plane.AdditionError)).Upper;
        if (error > 0.001f) return false;
        var contact = new ConvexLeafEvidence(true, true, true, -hi, -lo, qw, tw, -encodedNormal,
            ConvexPlanarFeatures.Classify(request.Shape, plane.Query), feature, id,
            worldQuery, worldTarget, normal, CertifiedErrorMetres: Up(error), IsOverlapping: true, Translation: plane.Translation);
        result = new(initial, attained, lo, hi, plane.Translation, contact, lowerProof);
        return true;
    }

    static GeometryVector Add(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));
    static bool Encode(GeometryVector bounds, out Vector3 value, out double error)
    {
        value = default; error = 0;
        if (!bounds.IsResolved) return false;
        value = new((float)(bounds.X.Lower * 0.5 + bounds.X.Upper * 0.5),
            (float)(bounds.Y.Lower * 0.5 + bounds.Y.Upper * 0.5), (float)(bounds.Z.Lower * 0.5 + bounds.Z.Upper * 0.5));
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) return false;
        Q Residual(GeometryInterval interval, float encoded)
        {
            Q a = Q.From(interval.Lower) - Q.From(encoded), b = Q.From(interval.Upper) - Q.From(encoded);
            return (a * a - b * b).Sign >= 0 ? a * a : b * b;
        }
        error = Q.RootBounds(Residual(bounds.X, value.X) + Residual(bounds.Y, value.Y) + Residual(bounds.Z, value.Z)).Upper;
        return true;
    }
    static float Down(double x) { float r = (float)x; return r > x ? MathF.BitDecrement(r) : r; }
    static float Up(double x) { float r = (float)x; return r < x ? MathF.BitIncrement(r) : r; }
}

/// <summary>Support optimality on the original pair, retaining the admission's lifetime and pose.</summary>
internal readonly struct ConvexPenetrationWitness(ConvexPenetrationOrigin owner, ConvexPenetrationPlane plane)
{
    internal ConvexQueryRequest Request => owner.Request;
    internal ConvexPlanarSource Source => owner.Source;
    internal ConvexCertificateFraction Fraction => owner.Fraction;
    internal P Feet => owner.Feet + P.From(plane.Translation);
    internal P Query => plane.Query;
    internal P Target => plane.Target - P.From(plane.Translation);
    internal P Direction => plane.Direction;
    internal bool IsAlive => owner.IsAlive && plane.Matches(owner);
}
