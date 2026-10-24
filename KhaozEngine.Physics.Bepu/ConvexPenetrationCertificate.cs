using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>An original-member minimum-depth enclosure. The attained KKT support pair is
/// independent of the global inner-hull lower proof. Both are required before encoding.</summary>
internal readonly struct ConvexPenetrationCertificate
{
    internal ConvexInitialEvidence Initial { get; }
    internal P Query { get; }
    internal P Target { get; }
    internal P Direction { get; }
    internal float LowerDepth { get; }
    internal float UpperDepth { get; }
    internal Vector3 Translation { get; }
    internal ConvexLeafEvidence Contact { get; }
    internal GeometryInterval GlobalLowerDepthBounds { get; }
    internal bool IsAlive => Initial.IsAlive && Contact.IsComplete;

    ConvexPenetrationCertificate(ConvexInitialEvidence initial, P query, P target, P direction,
        float lower, float upper, Vector3 translation, ConvexLeafEvidence contact, GeometryInterval lowerProof)
    {
        Initial = initial; Query = query; Target = target; Direction = direction;
        LowerDepth = lower; UpperDepth = upper; Translation = translation; Contact = contact;
        GlobalLowerDepthBounds = lowerProof;
    }

    internal static bool TryCreate(ConvexInitialEvidence initial, P q, P t, P n,
        ConvexTargetFeature feature, int id, GeometryInterval lowerProof, ref ConvexQueryWork work, out ConvexPenetrationCertificate result)
    {
        result = default;
        if (!initial.IsAlive || initial.State != ConvexInitialState.Overlapping || !lowerProof.IsResolved || lowerProof.Lower <= 0) return false;
        var pair = initial.Pair; var request = pair.Request;
        Q squared = P.Dot(n, n); if (squared.Sign <= 0) return false;
        GeometryInterval norm = Q.RootBounds(squared);
        GeometryVector unit = new(n.X.Bounds.Divide(norm), n.Y.Bounds.Divide(norm), n.Z.Bounds.Divide(norm));
        GeometryInterval margin = GeometryInterval.Exact(request.Shape.Margin);
        P delta = q - t;
        GeometryInterval depth = P.Dot(delta, n).Bounds.Divide(norm).Add(margin);
        if (!depth.IsResolved || depth.Lower <= 0) return false;
        GeometryVector movement = new(Negate(delta.X.Bounds.Add(unit.X.Multiply(margin))),
            Negate(delta.Y.Bounds.Add(unit.Y.Multiply(margin))), Negate(delta.Z.Bounds.Add(unit.Z.Multiply(margin))));
        float Away(GeometryInterval interval, Q component) => component.Sign == 0 ? 0 :
            component.Sign > 0 ? MathF.BitDecrement((float)interval.Lower) : MathF.BitIncrement((float)interval.Upper);
        Vector3 translation = new(Away(movement.X, n.X), Away(movement.Y, n.Y), Away(movement.Z, n.Z));
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z)) return false;
        Q m = Q.From(request.Shape.Margin);
        P feet = P.From(request.Pose.Position) + P.From(request.Displacement) * pair.Fraction.Value;
        double additionError = 0;
        bool cleared = false;
        // The operational position addition can round back into the original member. A
        // next-representable destination is only another proposal and earns both exact
        // support checks again. Its displacement and addition residual consume the budget.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!work.Feature()) return false;
            Vector3 destination = new((float)(feet.X.Bounds.Lower * 0.5 + feet.X.Bounds.Upper * 0.5) + translation.X,
                (float)(feet.Y.Bounds.Lower * 0.5 + feet.Y.Bounds.Upper * 0.5) + translation.Y,
                (float)(feet.Z.Bounds.Lower * 0.5 + feet.Z.Bounds.Upper * 0.5) + translation.Z);
            P operational = P.From(destination) - feet;
            Q gap = -P.Dot(n, delta + P.From(translation)), finalGap = -P.Dot(n, delta + operational);
            if (gap.Sign >= 0 && (gap * gap - m * m * squared).Sign >= 0 &&
                finalGap.Sign >= 0 && (finalGap * finalGap - m * m * squared).Sign >= 0)
            {
                P residual = operational - P.From(translation);
                additionError = Q.RootBounds(P.Dot(residual, residual)).Upper;
                cleared = true; break;
            }
            float Next(float value, Q component) => component.Sign == 0 ? value :
                component.Sign > 0 ? MathF.BitDecrement(value) : MathF.BitIncrement(value);
            destination = new(Next(destination.X, n.X), Next(destination.Y, n.Y), Next(destination.Z, n.Z));
            P step = P.From(destination) - feet;
            translation = new(Away(step.X.Bounds, n.X), Away(step.Y.Bounds, n.Y), Away(step.Z.Bounds, n.Z));
        }
        if (!cleared) return false;
        GeometryVector grown = new(q.X.Bounds.Add(unit.X.Multiply(margin)), q.Y.Bounds.Add(unit.Y.Multiply(margin)),
            q.Z.Bounds.Add(unit.Z.Multiply(margin)));
        GeometryVector worldQuery = Add(grown, feet.Bounds), worldTarget = (t + feet).Bounds;
        if (!Encode(worldQuery, out Vector3 qw, out double qe) || !Encode(worldTarget, out Vector3 tw, out double te) ||
            !Encode(unit, out Vector3 normal, out double ne) || normal == Vector3.Zero) return false;
        GeometryInterval encodedLength = Q.RootBounds(P.Dot(P.From(translation), P.From(translation)));
        float lo = Down(lowerProof.Lower), hi = Up(Math.Max(depth.Upper, encodedLength.Upper));
        double error = GeometryInterval.Exact((double)hi - lo).Add(GeometryInterval.Exact(qe)).Add(GeometryInterval.Exact(te))
            .Add(GeometryInterval.Exact(ne).Multiply(GeometryInterval.Exact(Math.Max(1, hi)))).Add(GeometryInterval.Exact(additionError)).Upper;
        if (!float.IsFinite(lo) || !float.IsFinite(hi) || hi < lo || error > 0.001f) return false;
        var contact = new ConvexLeafEvidence(true, true, true, -hi, -lo, qw, tw, -normal,
            ConvexPlanarFeatures.Classify(request.Shape, q), feature, id, worldQuery, worldTarget, unit,
            CertifiedErrorMetres: Up(error), IsOverlapping: true, Translation: translation);
        result = new(initial, q, t, n, lo, hi, translation, contact, lowerProof); return true;
    }

    static GeometryVector Add(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));
    static GeometryInterval Negate(GeometryInterval a) => GeometryInterval.Enclose(-a.Upper, -a.Lower);
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
