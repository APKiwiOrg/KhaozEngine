using System;
using KhaozEngine.Physics;
using static KhaozEngine.MapDoc.Physics.MapShapeQueries;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Distance and ray queries against a base-aligned cylinder in scalar double. Per-point distance and the cap
/// and side ray math are closed form. Distance from a probe segment minimises the closed-form point distance along the
/// segment with a deterministic, convergent golden section search. A band that cuts a tilted cylinder is measured on
/// the slice at the band plane the unclipped closest point crossed.</summary>
internal static class MapCylinderQueries
{
    internal static double Measure(in MapProbe probe, CylinderShape cylinder, MapDoublePose pose, MapSlab band)
    {
        double r = cylinder.Radius, length = cylinder.Length;
        double min = band.Min - pose.Position.Y, max = band.Max - pose.Position.Y;
        MapDouble3 p0 = probe.Bottom.Subtract(pose.Position), p1 = probe.Top.Subtract(pose.Position);
        MapDouble3 axis = pose.Rotate(new MapDouble3(0d, 1d, 0d));
        if (axis.X == 0d && axis.Z == 0d)
        {
            // Upright either way up: the band clips its height, and the upright probe separates into a radial and a
            // vertical gap.
            double low = Math.Max(Math.Min(0d, length * axis.Y), min), high = Math.Min(Math.Max(0d, length * axis.Y), max);
            if (low > high) return double.PositiveInfinity;
            double radial = Math.Max(Math.Sqrt(p0.X * p0.X + p0.Z * p0.Z) - r, 0d);
            double vertical = Gap(p0.Y, p1.Y, low, high);
            return Surface(Math.Sqrt(radial * radial + vertical * vertical), probe.Radius);
        }

        double xx = axis.X * axis.X, zz = axis.Z * axis.Z, n = xx + axis.Y * axis.Y + zz;
        double rise = r * Math.Sqrt((xx + zz) / n);
        double lowest = Math.Min(0d, length * axis.Y) - rise, highest = Math.Max(0d, length * axis.Y) + rise;
        if (highest < min || lowest > max) return double.PositiveInfinity;

        // The distance from a point to a solid cylinder is closed form and convex along the probe. A fixed golden
        // section search over the probe converges deterministically to its minimum.
        MapDoublePose inverse = Inverse(pose);
        MapDouble3 l0 = inverse.Rotate(p0), l1 = inverse.Rotate(p1);
        double core = GoldenMinimum(s => PointToCylinder(Lerp(l0, l1, s), r, length), 0d, 1d, out double at);
        if (lowest >= min && highest <= max) return Surface(core, probe.Radius);

        // A closest point inside the band is the answer. Otherwise the clipped answer lies on the band plane it
        // crossed, because the distance is convex over the cylinder.
        MapDouble3 closest = pose.Rotate(ClosestOnCylinder(Lerp(l0, l1, at), r, length));
        if (closest.Y >= min && closest.Y <= max) return Surface(core, probe.Radius);
        return Surface(SliceDistance(p0, p1, axis, r, length, closest.Y > max ? max : min), probe.Radius);
    }

    // The distance from the upright probe to the slice of a tilted cylinder at height k. Each cross-section disc meets
    // the plane in a horizontal chord, and the horizontal distance to the chord is convex along the axis.
    static double SliceDistance(MapDouble3 p0, MapDouble3 p1, MapDouble3 axis, double r, double length, double k)
    {
        double norm = Math.Sqrt(Dot(axis, axis));
        MapDouble3 a = axis.Scale(1d / norm);
        double extent = length * norm;
        double h = Math.Sqrt(a.X * a.X + a.Z * a.Z);
        var u = new MapDouble3(-a.Z / h, 0d, a.X / h);
        MapDouble3 v = MapDouble3.Cross(u, a);
        if (v.Y < 0d) v = v.Scale(-1d);
        double reachY = r * v.Y, sLow, sHigh;
        if (a.Y == 0d)
        {
            if (Math.Abs(k) > reachY) return double.PositiveInfinity;
            sLow = 0d;
            sHigh = extent;
        }
        else
        {
            double s1 = (k - reachY) / a.Y, s2 = (k + reachY) / a.Y;
            sLow = Math.Max(0d, Math.Min(s1, s2));
            sHigh = Math.Min(extent, Math.Max(s1, s2));
            if (sLow > sHigh) return double.PositiveInfinity;
        }
        double horizontal = GoldenMinimum(s =>
        {
            double beta = Math.Clamp((k - s * a.Y) / v.Y, -r, r), alpha = Math.Sqrt(r * r - beta * beta);
            MapDouble3 centre = a.Scale(s).Add(v.Scale(beta));
            return HorizontalToSegment(p0.X, p0.Z, centre.Subtract(u.Scale(alpha)), centre.Add(u.Scale(alpha)));
        }, sLow, sHigh, out _);
        double vertical = Gap(p0.Y, p1.Y, k, k);
        return Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    static double PointToCylinder(MapDouble3 p, double r, double length)
    {
        double radial = Math.Max(Math.Sqrt(p.X * p.X + p.Z * p.Z) - r, 0d);
        double axial = p.Y < 0d ? -p.Y : p.Y > length ? p.Y - length : 0d;
        return Math.Sqrt(radial * radial + axial * axial);
    }

    static MapDouble3 ClosestOnCylinder(MapDouble3 p, double r, double length)
    {
        double rho = Math.Sqrt(p.X * p.X + p.Z * p.Z), scale = rho > r ? r / rho : 1d;
        return new MapDouble3(p.X * scale, Math.Clamp(p.Y, 0d, length), p.Z * scale);
    }

    static double HorizontalToSegment(double x, double z, MapDouble3 a, MapDouble3 b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, lengthSquared = dx * dx + dz * dz;
        double s = lengthSquared == 0d ? 0d : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared, 0d, 1d);
        double ex = x - (a.X + s * dx), ez = z - (a.Z + s * dz);
        return Math.Sqrt(ex * ex + ez * ez);
    }

    // The vertical gap between [low0, high0] and [low1, high1].
    static double Gap(double low0, double high0, double low1, double high1) =>
        high1 < low0 ? low0 - high1 : low1 > high0 ? low1 - high0 : 0d;

    // A fixed-length golden section search for the minimum of a convex function on [low, high]. Its IEEE operations
    // run in a fixed order and its bracket shrinks below double resolution, so every head converges to the same value.
    // The best value seen wins, the first on a tie.
    static double GoldenMinimum(Func<double, double> f, double low, double high, out double at)
    {
        const int Iterations = 90;
        double ratio = (Math.Sqrt(5d) - 1d) * 0.5;
        at = low;
        double best = f(low);
        Consider(high, f(high), ref best, ref at);
        if (!(high > low)) return best;
        double a = low, b = high;
        double x1 = b - ratio * (b - a), x2 = a + ratio * (b - a);
        double f1 = f(x1), f2 = f(x2);
        Consider(x1, f1, ref best, ref at);
        Consider(x2, f2, ref best, ref at);
        for (int i = 0; i < Iterations; i++)
        {
            if (f1 <= f2)
            {
                b = x2;
                x2 = x1;
                f2 = f1;
                x1 = b - ratio * (b - a);
                f1 = f(x1);
                Consider(x1, f1, ref best, ref at);
            }
            else
            {
                a = x1;
                x1 = x2;
                f1 = f2;
                x2 = a + ratio * (b - a);
                f2 = f(x2);
                Consider(x2, f2, ref best, ref at);
            }
        }
        return best;
    }

    static void Consider(double x, double value, ref double best, ref double at)
    {
        if (value >= best) return;
        best = value;
        at = x;
    }

    internal static bool Cast(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, CylinderShape cylinder,
        MapDoublePose pose, out double t, out MapDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        MapDoublePose inverse = Inverse(pose);
        MapDouble3 o = inverse.Rotate(origin.Subtract(pose.Position)), d = inverse.Rotate(direction);
        double r = cylinder.Radius, length = cylinder.Length;

        // The slab between the base and the top cap.
        double enter = double.NegativeInfinity, exit = double.PositiveInfinity, cap = d.Y > 0d ? -1d : 1d;
        if (!ClipRay(o.Y, d.Y, 0d, length, 0d, ref enter, ref exit)) return false;

        // The infinite side wall.
        double sideEnter = double.NegativeInfinity, sideExit = double.PositiveInfinity;
        double qa = d.X * d.X + d.Z * d.Z, qb = o.X * d.X + o.Z * d.Z, qc = o.X * o.X + o.Z * o.Z - r * r;
        if (qa == 0d)
        {
            if (qc > 0d) return false;
        }
        else
        {
            double discriminant = qb * qb - qa * qc;
            if (discriminant < 0d) return false;
            double root = Math.Sqrt(discriminant);
            sideEnter = (-qb - root) / qa;
            sideExit = (-qb + root) / qa;
        }

        bool side = sideEnter > enter;
        double tEnter = Math.Max(enter, sideEnter), tExit = Math.Min(exit, sideExit);
        if (tEnter > tExit || tExit < tStart || tEnter > tEnd) return false;
        if (tEnter < tStart)
        {
            t = tStart;
            inside = true;
            return true;
        }
        t = tEnter;
        MapDouble3 hit = o.Add(d.Scale(tEnter));
        normal = pose.Rotate(side ? new MapDouble3(hit.X, 0d, hit.Z) : new MapDouble3(0d, cap, 0d));
        return true;
    }
}
