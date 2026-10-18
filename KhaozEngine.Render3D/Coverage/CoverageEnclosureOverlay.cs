using System;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using D = KhaozEngine.Render3D.CoverageScalarBounds;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;
using P = KhaozEngine.Render3D.CoverageEnclosurePredicates;
using S = KhaozEngine.Render3D.CoverageEnclosureSupport;

namespace KhaozEngine.Render3D;

/// <summary>Analytic face overlay, joining possible accepted vertices without enumerating branch histories.</summary>
internal static class CoverageEnclosureOverlay
{
    static readonly double crossingError = CrossingError();

    internal static I Maximum(CoverageReferenceSupport support, ReadOnlySpan<V> host, out int unboundedTriangle)
    {
        unboundedTriangle = -1;
        var maximum = new MaximumSet();
        Span<V> sheet = stackalloc V[3];
        for (int triangle = 0; triangle < support.Triangles.Length; triangle++)
        {
            var t = support.Triangles[triangle];
            sheet[0] = M.Point(support.Positions[t.A]); sheet[1] = M.Point(support.Positions[t.B]); sheet[2] = M.Point(support.Positions[t.C]);
            for (int i = 0; i < 3; i++)
            {
                if (P.InsidePossible(sheet[0], sheet[1], sheet[2], host[i], out bool certain))
                    Accumulate(host, sheet, host[i], certain, ref maximum);
                if (P.InsidePossible(host[0], host[1], host[2], sheet[i], out certain))
                    Accumulate(host, sheet, sheet[i], certain, ref maximum);
            }
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    V a = host[i], b = host[(i + 1) % 3], s = sheet[j], u = sheet[(j + 1) % 3];
                    var ca = P.Edge(s, u, a); var cb = P.Edge(s, u, b);
                    var ac = P.Edge(a, b, s); var ad = P.Edge(a, b, u);
                    if (P.SameStrictSide(ca, cb) || P.SameStrictSide(ac, ad)) continue;
                    if (ca.MinSign == 0 && ca.MaxSign == 0 && cb.MinSign == 0 && cb.MaxSign == 0) continue;
                    V otherHull = M.Hull(s, u);
                    // A device endpoint branch requires that endpoint to lie on the other finite segment.
                    if (ca.Zero && S.Overlap(a, otherHull)) Accumulate(host, sheet, a, false, ref maximum);
                    if (cb.Zero && S.Overlap(b, otherHull)) Accumulate(host, sheet, b, false, ref maximum);
                    if (P.OppositePossible(ca, cb) && Crossing(a, b, s, u, ca.Exact, cb.Exact, out I parameter))
                    {
                        V point = M.Add(a, M.Multiply(M.Subtract(b, a), parameter));
                        bool certain = !ca.Zero && !cb.Zero && !ac.Zero && !ad.Zero;
                        Accumulate(host, sheet, point, certain, ref maximum);
                    }
                }
            if (!maximum.Finite) { unboundedTriangle = t.Triangle; return F.Unbounded; }
        }
        return maximum.Result;
    }

    static void Accumulate(ReadOnlySpan<V> host, ReadOnlySpan<V> sheet, V p, bool certain, ref MaximumSet result)
    {
        I h = S.Height(host[0], host[1], host[2], p), s = S.Height(sheet[0], sheet[1], sheet[2], p);
        result.Include(F.Subtract(h, s), certain);
    }

    internal static bool Crossing(V a, V b, V s, V t, I ca, I cb, out I parameter)
    {
        D set = new(0, 1);
        D quotient = CoverageIntervals.Divide(new(ca.Min, ca.Max),
            CoverageIntervals.Subtract(new(ca.Min, ca.Max), new(cb.Min, cb.Max)));
        if (quotient.IsFinite) set = Intersect(set, quotient);
        D dx = CoverageIntervals.Subtract(B(b.X), B(a.X)), dy = CoverageIntervals.Subtract(B(b.Y), B(a.Y));
        D length = CoverageIntervals.Add(CoverageIntervals.Square(dx), CoverageIntervals.Square(dy));
        if (length.IsFinite && length.Min > 0)
        {
            D first = Project(s), second = Project(t);
            if (first.IsFinite && second.IsFinite)
                set = Intersect(set, new(Math.Min(first.Min, second.Min), Math.Max(first.Max, second.Max)));
        }
        parameter = default;
        if (!set.IsFinite) return false;
        // B30 bounds the exact intersection by geometry. The device's own scaled parameter lies in that set
        // enlarged by its NC1 store/subtract/divide error. This never clamps an unbounded quotient finite.
        parameter = new(Math.Max(0, Math.BitDecrement(set.Min - crossingError)), Math.Min(1, Math.BitIncrement(set.Max + crossingError)));
        return true;
        D Project(V p) => CoverageIntervals.Divide(CoverageIntervals.Add(
            CoverageIntervals.Multiply(CoverageIntervals.Subtract(B(p.X), B(a.X)), dx),
            CoverageIntervals.Multiply(CoverageIntervals.Subtract(B(p.Y), B(a.Y)), dy)), length);
    }

    // Uniform local formula error, not a world-space tolerance. Normalization puts the larger exact side in [1,2).
    // RN32 contributes at most 2^-24, stores use NC1 k=1, subtraction k=1, and Divide retains its expansion hull.
    static double CrossingError()
    {
        double store = Math.BitIncrement(Math.ScaleB(1, -24) + Math.BitIncrement(F.Store32(new(-2, 2)).Max - 2));
        double sum = Math.BitIncrement(2 * store);
        double subtract = Math.BitIncrement(F.Subtract(new(0, 2 + store), new(-2 - store, 0)).Max - (4 + sum));
        double denominator = Math.BitIncrement(sum + subtract);
        D ideal = CoverageIntervals.Divide(new(0, 2 + store), new(1 - denominator, 4 + denominator));
        I division = F.Divide(new(0, 2 + store), new(1 - denominator, 4 + denominator));
        double divisionError = Math.Max(Math.BitIncrement(division.Max - ideal.Max), Math.BitIncrement(ideal.Min - division.Min));
        D propagated = CoverageIntervals.Divide(CoverageIntervals.Add(new(store, store), new(denominator, denominator)),
            CoverageIntervals.Subtract(new(1, 1), new(denominator, denominator)));
        return Math.BitIncrement(propagated.Max + divisionError);
    }

    static D Intersect(D a, D b) => new(Math.Max(a.Min, b.Min), Math.Min(a.Max, b.Max));
    static D B(I a) => new(a.Min, a.Max);

    struct MaximumSet
    {
        bool any, certain, invalid;
        double minimum, guaranteed, maximum;
        internal readonly bool Finite => !invalid;
        internal readonly I Result => any && !invalid ? new(certain ? guaranteed : minimum, maximum) : F.Unbounded;
        internal void Include(I candidate, bool required)
        {
            if (!candidate.IsFinite) { invalid = true; return; }
            minimum = any ? Math.Min(minimum, candidate.Min) : candidate.Min;
            maximum = any ? Math.Max(maximum, candidate.Max) : candidate.Max;
            any = true;
            if (!required) return;
            guaranteed = certain ? Math.Max(guaranteed, candidate.Min) : candidate.Min;
            certain = true;
        }
    }
}
