using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact moving vertex/cylinder seeds, including a square-free double root. Closed
/// target and query support predicates decide feasibility. A failed seed never proves a miss.</summary>
internal static class ConvexSweepVertices
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P a, int id,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!work.Feature() || !work.Cell()) return false;
        P v = P.From(request.Displacement) * Q.From(-1);
        Q radius = Q.From(request.Shape.Radius), grown = radius + Q.From(request.Shape.Margin);
        Q aa = v.X * v.X + v.Z * v.Z;
        Q bb = Q.From(2) * (a.X * v.X + a.Z * v.Z);
        Q cc = a.X * a.X + a.Z * a.Z - grown * grown;
        foreach (Q time in Roots(aa, bb, cc))
        {
            if (!ConvexPlanarSweep.Closed(time)) continue;
            P t = a + v * time;
            if ((t.Y - Q.From(request.Shape.RingY)).Sign < 0 || (Q.From(request.Shape.UpperY) - t.Y).Sign < 0) continue;
            P q = new(t.X * radius / grown, t.Y, t.Z * radius / grown);
            P n = new(t.X, default, t.Z);
            if (ConvexPlanarSweep.TryHit(request, leaf, time, q, t, n, ConvexTargetFeature.Vertex, id,
                ref work, scratch, out evidence)) return true;
            if (work.Exhausted) return false;
        }
        return false;
    }

    internal static Q[] Roots(Q a, Q b, Q c)
    {
        if (!a.Rational || !b.Rational || !c.Rational) throw new ConvexExactRefusalException();
        if (a.Sign == 0) return b.Sign == 0 ? c.Sign == 0 ? [default] : [] : [-c / b];
        Q discriminant = b * b - Q.From(4) * a * c;
        if (discriminant.Sign < 0) return [];
        Q root = Q.Root(discriminant.A), denominator = Q.From(2) * a;
        return discriminant.Sign == 0 ? [-b / denominator] :
            a.Sign > 0 ? [(-b - root) / denominator, (-b + root) / denominator] :
                [(-b + root) / denominator, (-b - root) / denominator];
    }
}
