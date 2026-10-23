using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Finite line strata. Quadratic cone stationarity retains its original radical sign.
/// Oblique circular strata use the shared disc projection and certified root-region producer.</summary>
internal static class ConvexPlanarEdges
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P a, P v,
        int id, ref ConvexQueryWork work, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!work.Feature() || !work.Cell()) return false;
        Q norm = P.Dot(v, v);
        if (norm.Sign <= 0) return false;
        Q lower = Q.From(request.Shape.LowerY), ring = Q.From(request.Shape.RingY), upper = Q.From(request.Shape.UpperY);
        Q r = Q.From(request.Shape.Radius), h = ring - lower;
        P apex = new(default, lower, default);
        Q parameter = P.Dot(apex - a, v) / norm;
        if (Closed(parameter) && ConvexFeatureKernel.TryPair(request, leaf, apex, a + v * parameter,
            ConvexTargetFeature.Edge, id, ref work, out evidence)) return true;
        Q radialNorm = v.X * v.X + v.Z * v.Z, radialDot = a.X * v.X + a.Z * v.Z;
        if (radialNorm.Sign > 0)
        {
            parameter = -radialDot / radialNorm;
            if (Closed(parameter) && ConvexFeatureKernel.TryPoint(request, leaf, a + v * parameter,
                ConvexTargetFeature.Edge, id, ref work, out evidence)) return true;
        }
        if (v.Y.Sign != 0)
        {
            foreach (Q y in new[] { lower, ring, upper, (ring + upper) / Q.From(2) })
            {
                parameter = (y - a.Y) / v.Y;
                if (Closed(parameter) && ConvexFeatureKernel.TryPoint(request, leaf, a + v * parameter,
                    ConvexTargetFeature.Edge, id, ref work, out evidence)) return true;
            }
        }
        Q radialSquare = a.X * a.X + a.Z * a.Z;
        // Cone-line stationary equation h (a_h dot v_h + s |v_h|^2) = R vy rho.
        Q vv = r * r * v.Y * v.Y, hh = h * h;
        Q qa = hh * radialNorm * radialNorm - vv * radialNorm;
        Q qb = Q.From(2) * radialDot * (hh * radialNorm - vv);
        Q qc = hh * radialDot * radialDot - vv * radialSquare;
        if (v.Y.Sign != 0)
            foreach (Q s in Roots(qa, qb, qc))
            {
                if (!Closed(s)) continue;
                P t = a + v * s;
                Q rho = h * (radialDot + radialNorm * s) / (r * v.Y);
                if (rho.Sign <= 0 || (rho * rho - t.X * t.X - t.Z * t.Z).Sign != 0) continue;
                Q along = (r * rho + h * (t.Y - lower)) / (r * r + hh);
                P q = new(t.X * (r * along / rho), lower + h * along, t.Z * (r * along / rho));
                if (ConvexFeatureKernel.TryPair(request, leaf, q, t, ConvexTargetFeature.Edge, id, ref work, out evidence)) return true;
            }
        // Exact surface intersections give attained overlap witnesses without a normal proposal.
        foreach (bool cone in new[] { false, true })
        {
            Q scale = cone ? hh : Q.From(1), y = a.Y - lower;
            Q aa = scale * radialNorm - (cone ? r * r * v.Y * v.Y : default);
            Q bb = Q.From(2) * (scale * radialDot - (cone ? r * r * y * v.Y : default));
            Q cc = scale * radialSquare - r * r * (cone ? y * y : Q.From(1));
            foreach (Q s in Roots(aa, bb, cc))
            {
                if (!Closed(s) || !work.Feature() || !work.Cell()) continue;
                P t = a + v * s;
                if (ConvexPlanarFeatures.Member(request.Shape, t) &&
                    ConvexDistanceEncoding.TryCreate(request, t, t, ConvexTargetFeature.Edge, id, true, out evidence)) return true;
            }
        }
        evidence = default;
        return false;
    }

    static bool Closed(Q s) => s.Sign >= 0 && (Q.From(1) - s).Sign >= 0;

    static Q[] Roots(Q a, Q b, Q c)
    {
        if (!a.Rational || !b.Rational || !c.Rational) throw new ConvexExactRefusalException();
        if (a.Sign == 0) return b.Sign == 0 ? c.Sign == 0 ? [Q.From(0.5)] : [] : [-c / b];
        Q discriminant = b * b - Q.From(4) * a * c;
        if (discriminant.Sign < 0) return [];
        Q root = Q.Root(discriminant.A), denominator = Q.From(2) * a;
        return [(-b - root) / denominator, (-b + root) / denominator];
    }
}
