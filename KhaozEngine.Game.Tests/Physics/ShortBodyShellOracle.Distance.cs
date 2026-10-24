using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
    // Meridian reduction is independent of the production feature/KKT construction. A full
    // Cartesian box has independent height and radial intervals. Distance decreases with radial
    // approach to the axis. Below the shoulder it decreases with height toward that shoulder.
    // Other installed convex snapshots use independently certified support and attained bounds.
    internal static ConvexOracleEvent Entry(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, Vector3 displacement)
    {
        Validate(shape.Radius, shape.LowerY, shape.RingY, shape.UpperY, shape.Margin);
        if (pose.Orientation != Quaternion.Identity ||
            target.Vertices.Length < 4 || target.Faces.Length < 4) return default;
        if (displacement != Vector3.Zero) return SweepEntry(shape, pose, target, displacement);
        if (target.Vertices.Length != 8 || target.Faces.Length != 6) return GeneralEntry(shape, pose, target);
        R Copy(InstalledPoseOracle.R r) => new(r.Numerator, r.Denominator);
        R[] low = new R[3], high = new R[3];
        var points = new R[8][];
        for (int i = 0; i < 8; i++)
        {
            var v = target.Vertices[i];
            points[i] = [Copy(v.X) - R.From(pose.Position.X), Copy(v.Y) - R.From(pose.Position.Y), Copy(v.Z) - R.From(pose.Position.Z)];
            for (int j = 0; j < 3; j++)
            {
                low[j] = i == 0 ? points[i][j] : R.Min(low[j], points[i][j]);
                high[j] = i == 0 ? points[i][j] : R.Max(high[j], points[i][j]);
            }
        }
        int corners = 0;
        foreach (var p in points)
        {
            int mask = 0;
            for (int j = 0; j < 3; j++)
                if (p[j].CompareTo(high[j]) == 0) mask |= 1 << j;
                else if (p[j].CompareTo(low[j]) != 0) return GeneralEntry(shape, pose, target);
            if ((corners & (1 << mask)) != 0) return GeneralEntry(shape, pose, target);
            corners |= 1 << mask;
        }
        if (corners != 255) return GeneralEntry(shape, pose, target);
        R Near(R a, R b) => a.N.Sign > 0 ? a : b.N.Sign < 0 ? b : R.Zero;
        R x = Near(low[0], high[0]), z = Near(low[2], high[2]);
        R y = R.Max(low[1], R.Min(high[1], R.From(shape.RingY)));
        I best = MeridianDistance(shape, x, y, z);
        R margin = R.From(shape.Margin);
        var bounds = Outward(new(R.Max(R.Zero, best.Lower - margin), R.Max(R.Zero, best.Upper - margin)));
        return new(true, bounds.Lower, bounds.Upper);
    }

    static I MeridianDistance(in ConvexQueryShape shape, R x, R y, R z)
    {
        R radialSquare = x * x + z * z;
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        R allowed = y.CompareTo(ring) < 0 ? radius * (y - lower) / (ring - lower) : radius;
        if (y.CompareTo(lower) >= 0 && y.CompareTo(upper) <= 0 && radialSquare.CompareTo(allowed * allowed) <= 0)
            return I.Point(R.Zero);
        I best = default;
        bool first = true;
        foreach (var residual in MeridianResiduals(shape, x, y, z))
        {
            I distance = Sqrt(Square(residual.X) + Square(residual.Y));
            best = first ? distance : new(R.Min(best.Lower, distance.Lower), R.Min(best.Upper, distance.Upper));
            first = false;
        }
        return best;
    }

    static IEnumerable<(I Radial, I X, I Y)> MeridianResiduals(ConvexQueryShape shape, R x, R y, R z)
    {
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        I radial = Root(x * x + z * z), yi = I.Point(y);
        (R X, R Y)[] polygon = [(R.Zero, lower), (radius, ring), (radius, upper), (R.Zero, upper)];
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            R dx = b.X - a.X, dy = b.Y - a.Y;
            I fraction = Divide((radial - I.Point(a.X)) * dx + (yi - I.Point(a.Y)) * dy, dx * dx + dy * dy);
            fraction = new(R.Max(R.Zero, R.Min(R.One, fraction.Lower)), R.Max(R.Zero, R.Min(R.One, fraction.Upper)));
            I rx = radial - (I.Point(a.X) + fraction * dx);
            I ry = yi - (I.Point(a.Y) + fraction * dy);
            yield return (radial, rx, ry);
        }
    }

    // A high-precision meridian residual proposes a rational direction. Only the independent
    // complete support plane over every target vertex can prove separation, at any distance.
    static R[] MeridianDirection(in ConvexQueryShape shape, R[] p)
    {
        (I Radial, I X, I Y) best = default;
        I distance = default;
        bool first = true;
        foreach (var residual in MeridianResiduals(shape, p[0], p[1], p[2]))
        {
            I square = Square(residual.X) + Square(residual.Y);
            if (first || square.Upper.CompareTo(distance.Upper) < 0) { best = residual; distance = square; }
            first = false;
        }
        R Mid(I value) => (value.Lower + value.Upper) / new R(2, 1);
        R radial = Mid(best.Radial), x = Mid(best.X), y = Mid(best.Y);
        return radial.N.IsZero ? [R.Zero, y, R.Zero] : [x * p[0] / radial, y, x * p[2] / radial];
    }

    // Exact membership for represented witnesses. This intentionally does not repair a rounded
    // off-surface witness or accept a near-zero normal-cone residual with an epsilon.
    internal static bool NormalConeContains(in ConvexQueryShape shape, Vector3 queryWitness, Vector3 queryOutwardNormal)
    {
        if (shape.Margin != 0) return MarginNormalConeContains(shape, queryWitness, queryOutwardNormal);
        R x = R.From(queryWitness.X), y = R.From(queryWitness.Y), z = R.From(queryWitness.Z);
        R nx = R.From(queryOutwardNormal.X), ny = R.From(queryOutwardNormal.Y), nz = R.From(queryOutwardNormal.Z);
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        R radialSquare = x * x + z * z;
        R allowed = y.CompareTo(ring) < 0 ? radius * (y - lower) / (ring - lower) : radius;
        if (y.CompareTo(lower) < 0 || y.CompareTo(upper) > 0 || radialSquare.CompareTo(allowed * allowed) > 0) return false;
        R attained = x * nx + y * ny + z * nz;
        if ((attained - lower * ny).N.Sign < 0) return false;
        foreach (R height in new[] { ring, upper })
        {
            R gap = attained - height * ny;
            if (gap.N.Sign < 0 || (gap * gap - radius * radius * (nx * nx + nz * nz)).N.Sign < 0) return false;
        }
        return true;
    }

    static I Divide(I value, R divisor) => new(value.Lower / divisor, value.Upper / divisor);
    static I Square(I value)
    {
        R a = value.Lower * value.Lower, b = value.Upper * value.Upper;
        return new(value.Lower.N.Sign <= 0 && value.Upper.N.Sign >= 0 ? R.Zero : R.Min(a, b), R.Max(a, b));
    }
    static I Sqrt(I value) => new(Root(value.Lower).Lower, Root(value.Upper).Upper);
}
