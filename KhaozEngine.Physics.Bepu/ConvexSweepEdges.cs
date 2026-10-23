using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Moving segment stationarity on the cylinder and lower cone, plus the closed head
/// disc. These attained seeds require the common-normal and whole-prefix certificates.</summary>
internal static class ConvexSweepEdges
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        P a, P v, int id, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!work.Feature() || !work.Cell()) return false;
        P w = P.From(request.Displacement) * Q.From(-1);
        Q radius = Q.From(request.Shape.Radius), grown = radius + Q.From(request.Shape.Margin);
        Q radial = Horizontal(v, v);
        Q s0 = radial.Sign > 0 ? -Horizontal(a, v) / radial : default;
        Q s1 = radial.Sign > 0 ? -Horizontal(w, v) / radial : default;
        P start = a + v * s0, velocity = w + v * s1;
        foreach (Q time in ConvexSweepVertices.Roots(Horizontal(velocity, velocity),
            Q.From(2) * Horizontal(start, velocity), Horizontal(start, start) - grown * grown))
        {
            if (!ConvexPlanarSweep.Closed(time)) continue;
            Q s = s0 + s1 * time;
            if (radial.Sign == 0 && v.Y.Sign != 0)
                s = Clamp((Q.From((double)request.Shape.RingY * 0.5 + (double)request.Shape.UpperY * 0.5) - a.Y - w.Y * time) / v.Y);
            if (!ConvexPlanarSweep.Closed(s)) continue;
            P t = a + w * time + v * s;
            if ((t.Y - Q.From(request.Shape.RingY)).Sign < 0 || (Q.From(request.Shape.UpperY) - t.Y).Sign < 0) continue;
            P q = new(t.X * radius / grown, t.Y, t.Z * radius / grown);
            P n = new(t.X, default, t.Z);
            if (ConvexPlanarSweep.TryHit(request, leaf, time, q, t, n, ConvexTargetFeature.Edge, id,
                ref work, scratch, out evidence)) return true;
            if (work.Exhausted) return false;
        }
        if (v.Y.Sign == 0 && w.Y.Sign != 0)
        {
            Q time = (Q.From(request.Shape.UpperY) + Q.From(request.Shape.Margin) - a.Y) / w.Y;
            if (ConvexPlanarSweep.Closed(time))
            {
                P at = a + w * time;
                Q s = radial.Sign > 0 ? Clamp(-Horizontal(at, v) / radial) : default;
                P t = at + v * s, q = new(t.X, Q.From(request.Shape.UpperY), t.Z);
                if (ConvexPlanarSweep.TryHit(request, leaf, time, q, t, new(default, Q.From(1), default),
                    ConvexTargetFeature.Edge, id, ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
            }
        }
        if (request.Shape.Margin != 0) return false;
        Q r2 = radius * radius, h = Q.From(request.Shape.RingY) - Q.From(request.Shape.LowerY), h2 = h * h;
        P relative = a - new P(default, Q.From(request.Shape.LowerY), default);
        Q coefficient = h2 * Horizontal(v, v) - r2 * v.Y * v.Y;
        if (coefficient.Sign != 0)
        {
            s0 = -(h2 * Horizontal(relative, v) - r2 * relative.Y * v.Y) / coefficient;
            s1 = -(h2 * Horizontal(w, v) - r2 * w.Y * v.Y) / coefficient;
            start = relative + v * s0; velocity = w + v * s1;
            foreach (Q time in ConvexSweepVertices.Roots(h2 * Horizontal(velocity, velocity) - r2 * velocity.Y * velocity.Y,
                Q.From(2) * (h2 * Horizontal(start, velocity) - r2 * start.Y * velocity.Y),
                h2 * Horizontal(start, start) - r2 * start.Y * start.Y))
            {
                Q s = s0 + s1 * time;
                if (!ConvexPlanarSweep.Closed(time) || !ConvexPlanarSweep.Closed(s)) continue;
                P t = a + w * time + v * s;
                if ((t.Y - Q.From(request.Shape.LowerY)).Sign < 0 || (Q.From(request.Shape.RingY) - t.Y).Sign < 0) continue;
                P n = new(h2 * t.X, -r2 * (t.Y - Q.From(request.Shape.LowerY)), h2 * t.Z);
                if (ConvexPlanarSweep.TryHit(request, leaf, time, t, t, n, ConvexTargetFeature.Edge, id,
                    ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
            }
        }
        return false;
    }

    static Q Horizontal(P a, P b) => a.X * b.X + a.Z * b.Z;
    static Q Clamp(Q s) => s.Sign < 0 ? default : (Q.From(1) - s).Sign < 0 ? Q.From(1) : s;
}
