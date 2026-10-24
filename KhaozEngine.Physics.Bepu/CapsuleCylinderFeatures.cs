using System;
using static KhaozEngine.Physics.Bepu.CapsuleCylinderFrame;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed side, cap-disc and circular-rim predicates. A numerical closest point only
/// proposes a direction or axis parameter. Publication requires the independent predicates below.</summary>
internal static class CapsuleCylinderFeatures
{
    internal readonly record struct Proposal(double AxisParameter, FeaturePoint Normal);

    internal static bool TryPropose(in CapsuleCylinderFrame f, double t, ref CapsuleSweepWork work, out Proposal proposal)
    {
        proposal = default;
        double lo = -f.QueryHalf, hi = f.QueryHalf;
        if (!Evaluate(lo, f, t, ref work, out var normal, out double derivative)) return false;
        if (derivative >= 0) { proposal = new(lo, normal); return true; }
        if (!Evaluate(hi, f, t, ref work, out normal, out derivative)) return false;
        if (derivative <= 0) { proposal = new(hi, normal); return true; }
        for (int iteration = 0; iteration < 12; iteration++)
        {
            double s = lo + (hi - lo) * 0.5;
            if (!Evaluate(s, f, t, ref work, out normal, out derivative)) return false;
            if (derivative == 0) { proposal = new(s, normal); return true; }
            if (derivative > 0) hi = s;
            else lo = s;
        }
        double chosen = lo + (hi - lo) * 0.5;
        if (!Evaluate(chosen, f, t, ref work, out normal, out _)) return false;
        proposal = new(chosen, normal);
        return true;
    }

    static bool Evaluate(double s, in CapsuleCylinderFrame f, double t, ref CapsuleSweepWork work,
        out FeaturePoint normal, out double derivative)
    {
        normal = default; derivative = 0;
        if (!work.Feature()) return false;
        FeaturePoint p = f.Point(t, s);
        double px = Mid(p.X), py = Mid(p.Y), pz = Mid(p.Z);
        double ex = Mid(f.CylinderAxis.X), ey = Mid(f.CylinderAxis.Y), ez = Mid(f.CylinderAxis.Z);
        CircularProjection.Normal<double, CircularDoubleArithmetic>(px, py, pz, ex, ey, ez, f.Radius, f.HalfLength,
            out double x, out double y, out double z, out _);
        normal = new(FeatureNumber.Exact(x), FeatureNumber.Exact(y), FeatureNumber.Exact(z));
        derivative = x * Mid(f.QueryAxis.X) + y * Mid(f.QueryAxis.Y) + z * Mid(f.QueryAxis.Z);
        return normal.IsResolved && double.IsFinite(derivative);
    }

    static double Mid(FeatureNumber value) => value.Bounds.Lower * 0.5 + value.Bounds.Upper * 0.5;

    internal static bool Separated(in CapsuleCylinderFrame f, FeaturePoint n, double from, double to)
    {
        // Exact support of a finite circular cylinder: H |n.e| + R |n cross e|.
        // This encloses every side point, both cap discs and both entire rim circles.
        // Minima of the original affine path and axis projections cover the complete cell.
        FeatureNumber slope = FeaturePoint.Dot(n, f.Delta);
        FeatureNumber time = FeatureNumber.Enclosed(GeometryInterval.Enclose(from, to));
        if (from == to) time = FeatureNumber.Exact(from);
        FeatureNumber projected = FeaturePoint.Dot(n, f.Centre).Add(slope.Multiply(time, true));
        FeaturePoint cross = FeaturePoint.Cross(n, f.CylinderAxis, true);
        FeatureNumber support = Abs(FeaturePoint.Dot(n, f.CylinderAxis)).Multiply(FeatureNumber.Exact(f.HalfLength), true)
            .Add(Root(FeaturePoint.Dot(cross, cross)).Multiply(FeatureNumber.Exact(f.Radius), true))
            .Add(Abs(FeaturePoint.Dot(n, f.QueryAxis)).Multiply(FeatureNumber.Exact(f.QueryHalf), true))
            .Add(Root(FeaturePoint.Dot(n, n)).Multiply(FeatureNumber.Exact(f.QueryRadius), true));
        return projected.Subtract(support).Sign == GeometrySign.Positive;
    }

    internal static bool Contact(in CapsuleCylinderFrame f, double t, double s, SweepExactWorkspace w)
    {
        if (s < -f.QueryHalf || s > f.QueryHalf || t < 0 || t > 1) return false;
        FeaturePoint p = f.Point(t, s), e = f.CylinderAxis;
        FeatureNumber axial = Abs(FeaturePoint.Dot(p, e)).Subtract(FeatureNumber.Exact(f.HalfLength));
        if (axial.IsResolved) axial = axial.Bounds.Upper <= 0 ? FeatureNumber.Exact(0) :
            axial.Bounds.Lower >= 0 ? axial : FeatureNumber.Enclosed(GeometryInterval.Enclose(0, axial.Bounds.Upper));
        FeaturePoint cross = FeaturePoint.Cross(p, e, true);
        FeatureNumber radial = Root(FeaturePoint.Dot(cross, cross)).Subtract(FeatureNumber.Exact(f.Radius));
        if (radial.IsResolved) radial = radial.Bounds.Upper <= 0 ? FeatureNumber.Exact(0) :
            radial.Bounds.Lower >= 0 ? radial : FeatureNumber.Enclosed(GeometryInterval.Enclose(0, radial.Bounds.Upper));
        if (!axial.IsResolved || !radial.IsResolved) return false;
        GeometryInterval distance = axial.Bounds.Square().Add(radial.Bounds.Square());
        if (distance.IsResolved && distance.Upper <= (double)f.QueryRadius * f.QueryRadius) return true;
        if (!p.IsExact || !e.IsExact) return false;
        // Exact fallback retains the original point and unit-axis operands. For the rim,
        // (rho-R)^2 + z^2 <= r^2 becomes T <= 0 or T^2 <= 4 R^2 rho^2,
        // with rho>R proved before squaring. No sampled or polygonal circle is introduced.
        w.Reset();
        int px = w.Constant(p.X.Value), py = w.Constant(p.Y.Value), pz = w.Constant(p.Z.Value);
        int ex = w.Constant(e.X.Value), ey = w.Constant(e.Y.Value), ez = w.Constant(e.Z.Value);
        int dot = w.Add(w.Add(w.Multiply(px, ex), w.Multiply(py, ey)), w.Multiply(pz, ez));
        GeometrySign sign = w.Sign(dot);
        if (sign == GeometrySign.Unresolved) return false;
        int z = w.Subtract(sign == GeometrySign.Negative ? w.Negate(dot) : dot, w.Constant(f.HalfLength));
        GeometrySign zs = w.Sign(z);
        if (zs == GeometrySign.Unresolved) return false;
        if (zs != GeometrySign.Positive) z = w.Constant(0);
        int cx = w.Subtract(w.Multiply(py, ez), w.Multiply(pz, ey));
        int cy = w.Subtract(w.Multiply(pz, ex), w.Multiply(px, ez));
        int cz = w.Subtract(w.Multiply(px, ey), w.Multiply(py, ex));
        int rho2 = w.Add(w.Add(w.Square(cx), w.Square(cy)), w.Square(cz));
        int r2 = w.Square(w.Constant(f.QueryRadius)), R2 = w.Square(w.Constant(f.Radius)), z2 = w.Square(z);
        GeometrySign outside = w.Sign(w.Subtract(rho2, R2));
        if (outside == GeometrySign.Unresolved) return false;
        if (outside != GeometrySign.Positive) return NonPositive(w.Sign(w.Subtract(z2, r2))) && w.IsResolved;
        var arithmetic = new CircularSweepArithmetic(w);
        int difference = CircularContactEquation.Difference(rho2, R2, z2, r2, arithmetic);
        GeometrySign ds = w.Sign(difference);
        if (ds == GeometrySign.Unresolved) return false;
        return (NonPositive(ds) || NonPositive(w.Sign(CircularContactEquation.SquaredDifference(difference, R2, rho2, arithmetic)))) && w.IsResolved;
    }

    static bool NonPositive(GeometrySign sign) => sign is GeometrySign.Zero or GeometrySign.Negative;
}
