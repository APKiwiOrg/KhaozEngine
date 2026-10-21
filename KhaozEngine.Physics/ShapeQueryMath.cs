using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Scalar double GJK over point sets: the distance between two convex hulls and a ray cast against one.
/// Support ties resolve to the lowest point index and every loop runs in a fixed order, so equal inputs give equal
/// answers on every platform. A set of one or two points is a point or a segment.</summary>
internal static class ShapeQueryMath
{
    const int MaxIterations = 96;

    // Distance stops once the gap between its upper and lower bound is this fraction of the squared distance.
    const double DistanceTolerance = 1e-12;

    // A ray cast converges once the simplex distance is this fraction of its farthest vertex.
    const double CastTolerance = 1e-9, LooseCastTolerance = 1e-6;

    /// <summary>The distance between conv(<paramref name="a"/>) and conv(<paramref name="b"/>), zero when they
    /// overlap.</summary>
    internal static double Distance(ReadOnlySpan<ShapeDouble3> a, ReadOnlySpan<ShapeDouble3> b)
    {
        if (a.IsEmpty || b.IsEmpty) throw new ArgumentException("Both point sets need at least one point.");
        Span<ShapeDouble3> w = stackalloc ShapeDouble3[4];
        Span<int> ia = stackalloc int[4];
        Span<int> ib = stackalloc int[4];
        int n = 1;
        w[0] = a[0].Subtract(b[0]);
        ia[0] = 0;
        ib[0] = 0;
        ShapeDouble3 v = w[0];
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            double vv = Dot(v, v);
            if (vv == 0d) return 0d;
            int sa = Support(a, v.Scale(-1d)), sb = Support(b, v);
            ShapeDouble3 p = a[sa].Subtract(b[sb]);
            if (vv - Dot(v, p) <= DistanceTolerance * vv) break;
            bool repeated = false;
            for (int i = 0; i < n; i++) repeated |= ia[i] == sa && ib[i] == sb;
            if (repeated) break;
            w[n] = p;
            ia[n] = sa;
            ib[n] = sb;
            n++;
            ShapeDouble3 next = Closest(w, ia, ib, ref n);
            if (n == 4) return 0d;
            if (Dot(next, next) >= vv) break;
            v = next;
        }
        return Math.Sqrt(Dot(v, v));
    }

    /// <summary>Casts <paramref name="start"/> + t <paramref name="direction"/> for t in [0,
    /// <paramref name="maxT"/>] against conv(<paramref name="points"/>). A hit reports its parameter and the outward
    /// normal, which is zero when the start already lies in the hull.</summary>
    internal static bool Raycast(ReadOnlySpan<ShapeDouble3> points, ShapeDouble3 start, ShapeDouble3 direction, double maxT,
        out double t, out ShapeDouble3 normal)
    {
        if (points.IsEmpty) throw new ArgumentException("A hull needs at least one point.", nameof(points));
        t = 0d;
        normal = default;
        ShapeDouble3 x = start;
        Span<ShapeDouble3> w = stackalloc ShapeDouble3[4];
        Span<int> index = stackalloc int[4];
        Span<int> ignored = stackalloc int[4];
        int n = 0;
        ShapeDouble3 v = x.Subtract(points[0]);
        double vv;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            vv = Dot(v, v);
            if (vv == 0d || (n > 0 && vv <= CastTolerance * CastTolerance * Farthest(w, n))) return true;
            int sp = Support(points, v);
            ShapeDouble3 p = x.Subtract(points[sp]);
            double vp = Dot(v, p);
            bool present = false;
            for (int i = 0; i < n; i++) present |= index[i] == sp;
            if (vp > 0d)
            {
                double vr = Dot(v, direction);
                if (vr >= 0d) return false;
                t -= vp / vr;
                if (t > maxT) return false;
                x = start.Add(direction.Scale(t));
                normal = v;
            }
            else if (present)
            {
                // Neither advancing nor growing the simplex, so the next iteration would repeat this one.
                break;
            }
            if (!present)
            {
                index[n] = sp;
                n++;
            }
            for (int i = 0; i < n; i++) w[i] = x.Subtract(points[index[i]]);
            v = Closest(w, index, ignored, ref n);
            if (n == 4) return true;
        }
        vv = Dot(v, v);
        return n > 0 && vv <= LooseCastTolerance * LooseCastTolerance * Farthest(w, n);
    }

    /// <summary>The points of conv(<paramref name="points"/>) clipped to minY &lt;= y &lt;= maxY, as a point set with
    /// the same hull: every point inside the slab, and every crossing of a slab plane by a segment between two points.
    /// Empty when nothing of the hull lies in the slab.</summary>
    internal static ShapeDouble3[] ClipToSlab(ReadOnlySpan<ShapeDouble3> points, double minY, double maxY)
    {
        var clipped = new List<ShapeDouble3>();
        foreach (ShapeDouble3 p in points)
            if (p.Y >= minY && p.Y <= maxY) clipped.Add(p);
        for (int i = 0; i < points.Length; i++)
            for (int j = i + 1; j < points.Length; j++)
            {
                Crossing(points[i], points[j], minY, clipped);
                Crossing(points[i], points[j], maxY, clipped);
            }
        return clipped.ToArray();
    }

    static void Crossing(ShapeDouble3 a, ShapeDouble3 b, double y, List<ShapeDouble3> output)
    {
        if (!((a.Y < y && b.Y > y) || (a.Y > y && b.Y < y))) return;
        double s = (y - a.Y) / (b.Y - a.Y);
        output.Add(new ShapeDouble3(a.X + s * (b.X - a.X), y, a.Z + s * (b.Z - a.Z)));
    }

    internal static double Dot(ShapeDouble3 a, ShapeDouble3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>The point of triangle abc nearest <paramref name="point"/>.</summary>
    internal static ShapeDouble3 ClosestOnTriangle(ShapeDouble3 point, ShapeDouble3 a, ShapeDouble3 b, ShapeDouble3 c) =>
        OnTriangle(a.Subtract(point), b.Subtract(point), c.Subtract(point), out _).Add(point);

    static int Support(ReadOnlySpan<ShapeDouble3> points, ShapeDouble3 direction)
    {
        int best = 0;
        double bestDot = Dot(points[0], direction);
        for (int i = 1; i < points.Length; i++)
        {
            double d = Dot(points[i], direction);
            if (d > bestDot)
            {
                bestDot = d;
                best = i;
            }
        }
        return best;
    }

    static double Farthest(ReadOnlySpan<ShapeDouble3> w, int n)
    {
        double farthest = 0d;
        for (int i = 0; i < n; i++) farthest = Math.Max(farthest, Dot(w[i], w[i]));
        return farthest;
    }

    // The point of the simplex nearest the origin. The simplex keeps only the vertices that support it, in their
    // original order, with their index pairs. Four vertices remain only when the origin lies inside.
    static ShapeDouble3 Closest(Span<ShapeDouble3> w, Span<int> ia, Span<int> ib, ref int n)
    {
        int mask;
        ShapeDouble3 closest;
        switch (n)
        {
            case 1:
                return w[0];
            case 2:
                closest = OnSegment(w[0], w[1], out mask);
                break;
            case 3:
                closest = OnTriangle(w[0], w[1], w[2], out mask);
                break;
            default:
                closest = OnTetrahedron(w[0], w[1], w[2], w[3], out mask);
                if (mask == 0b1111) return default;
                break;
        }
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            w[kept] = w[i];
            ia[kept] = ia[i];
            ib[kept] = ib[i];
            kept++;
        }
        n = kept;
        return closest;
    }

    static ShapeDouble3 OnSegment(ShapeDouble3 a, ShapeDouble3 b, out int mask)
    {
        ShapeDouble3 ab = b.Subtract(a);
        double length = Dot(ab, ab);
        double s = length == 0d ? 0d : -Dot(a, ab) / length;
        if (s <= 0d)
        {
            mask = 0b01;
            return a;
        }
        if (s >= 1d)
        {
            mask = 0b10;
            return b;
        }
        mask = 0b11;
        return a.Add(ab.Scale(s));
    }

    // The closest point of triangle abc to the origin, by Voronoi regions. A degenerate triangle falls back to its
    // edges.
    static ShapeDouble3 OnTriangle(ShapeDouble3 a, ShapeDouble3 b, ShapeDouble3 c, out int mask)
    {
        ShapeDouble3 ab = b.Subtract(a), ac = c.Subtract(a);
        ShapeDouble3 ap = a.Scale(-1d);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0d && d2 <= 0d)
        {
            mask = 0b001;
            return a;
        }
        ShapeDouble3 bp = b.Scale(-1d);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0d && d4 <= d3)
        {
            mask = 0b010;
            return b;
        }
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0d && d1 >= 0d && d3 <= 0d)
        {
            mask = 0b011;
            return a.Add(ab.Scale(d1 / (d1 - d3)));
        }
        ShapeDouble3 cp = c.Scale(-1d);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0d && d5 <= d6)
        {
            mask = 0b100;
            return c;
        }
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0d && d2 >= 0d && d6 <= 0d)
        {
            mask = 0b101;
            return a.Add(ac.Scale(d2 / (d2 - d6)));
        }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0d && d4 - d3 >= 0d && d5 - d6 >= 0d)
        {
            mask = 0b110;
            return b.Add(c.Subtract(b).Scale((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        }
        double sum = va + vb + vc;
        if (sum <= 0d) return OnTriangleEdges(a, b, c, out mask);
        mask = 0b111;
        return a.Add(ab.Scale(vb / sum)).Add(ac.Scale(vc / sum));
    }

    static ShapeDouble3 OnTriangleEdges(ShapeDouble3 a, ShapeDouble3 b, ShapeDouble3 c, out int mask)
    {
        ShapeDouble3 best = OnSegment(a, b, out int m);
        mask = m;
        ShapeDouble3 candidate = OnSegment(b, c, out m);
        if (Dot(candidate, candidate) < Dot(best, best))
        {
            best = candidate;
            mask = m << 1;
        }
        candidate = OnSegment(c, a, out m);
        if (Dot(candidate, candidate) < Dot(best, best))
        {
            best = candidate;
            mask = ((m & 1) << 2) | ((m >> 1) & 1);
        }
        return best;
    }

    // Each face is tested when the origin lies on its far side from the fourth vertex, or when the tetrahedron is
    // flat against it. No such face means the origin is inside.
    static ShapeDouble3 OnTetrahedron(ShapeDouble3 a, ShapeDouble3 b, ShapeDouble3 c, ShapeDouble3 d, out int mask)
    {
        mask = 0b1111;
        ShapeDouble3 best = default;
        double bestDistance = double.PositiveInfinity;
        Face(a, b, c, d, 0, 1, 2, ref best, ref bestDistance, ref mask);
        Face(a, c, d, b, 0, 2, 3, ref best, ref bestDistance, ref mask);
        Face(a, d, b, c, 0, 3, 1, ref best, ref bestDistance, ref mask);
        Face(b, d, c, a, 1, 3, 2, ref best, ref bestDistance, ref mask);
        return best;
    }

    static void Face(ShapeDouble3 p, ShapeDouble3 q, ShapeDouble3 r, ShapeDouble3 opposite, int ip, int iq, int ir,
        ref ShapeDouble3 best, ref double bestDistance, ref int mask)
    {
        ShapeDouble3 normal = ShapeDouble3.Cross(q.Subtract(p), r.Subtract(p));
        double origin = -Dot(p, normal), far = Dot(opposite.Subtract(p), normal);
        if (far != 0d && origin * far >= 0d) return;
        ShapeDouble3 closest = OnTriangle(p, q, r, out int faceMask);
        double distance = Dot(closest, closest);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = closest;
        mask = ((faceMask & 1) << ip) | (((faceMask >> 1) & 1) << iq) | (((faceMask >> 2) & 1) << ir);
    }
    internal static Vector3 Unit(ShapeDouble3 v)
    {
        double length = Math.Sqrt(Dot(v, v));
        return length == 0d ? Vector3.Zero : new Vector3((float)(v.X / length), (float)(v.Y / length), (float)(v.Z / length));
    }

    internal static ShapeDoublePose Inverse(ShapeDoublePose pose) => new(default, -pose.Qx, -pose.Qy, -pose.Qz, pose.Qw);

    // Points rotated into the frame that is world-aligned and centred on the pose position, so far-from-origin worlds
    // keep their precision.
    internal static ShapeDouble3[] Centred(ShapeDoublePose pose, Vector3[] local)
    {
        var points = new ShapeDouble3[local.Length];
        for (int i = 0; i < local.Length; i++) points[i] = pose.Rotate(ShapeDouble3.From(local[i]));
        return points;
    }

    internal static double Surface(double core, double radius) => Math.Max(core - radius, 0d);

    internal static ShapeDouble3 Lerp(ShapeDouble3 a, ShapeDouble3 b, double s) => a.Add(b.Subtract(a).Scale(s));

    /// <summary>Clips the ray parameter window [<paramref name="tStart"/>, <paramref name="tEnd"/>] to the slab from
    /// <paramref name="min"/> less <paramref name="padding"/> to <paramref name="max"/> plus it on one axis. False when
    /// the window empties, or when a ray parallel to the slab lies outside it.</summary>
    internal static bool ClipRay(double origin, double direction, double min, double max, double padding,
        ref double tStart, ref double tEnd)
    {
        if (direction == 0d) return origin >= min - padding && origin <= max + padding;
        double a = (min - padding - origin) / direction, b = (max + padding - origin) / direction;
        tStart = Math.Max(tStart, Math.Min(a, b));
        tEnd = Math.Min(tEnd, Math.Max(a, b));
        return tStart <= tEnd;
    }

}

/// <summary>A double-precision vector for the envelope math.</summary>
internal readonly record struct ShapeDouble3(double X, double Y, double Z)
{
    internal static ShapeDouble3 From(Vector3 v) => new(v.X, v.Y, v.Z);
    internal ShapeDouble3 Add(ShapeDouble3 o) => new(X + o.X, Y + o.Y, Z + o.Z);
    internal ShapeDouble3 Subtract(ShapeDouble3 o) => new(X - o.X, Y - o.Y, Z - o.Z);
    internal ShapeDouble3 Scale(double s) => new(X * s, Y * s, Z * s);
    internal static ShapeDouble3 Cross(ShapeDouble3 a, ShapeDouble3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>A rigid pose in double precision. Orientations compose as parent times child, matching the physics
/// seam's compound flattening.</summary>
internal readonly record struct ShapeDoublePose(ShapeDouble3 Position, double Qx, double Qy, double Qz, double Qw)
{
    internal static ShapeDoublePose From(Pose pose) => new(ShapeDouble3.From(pose.Position),
        pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);

    internal ShapeDouble3 Rotate(ShapeDouble3 v)
    {
        var q = new ShapeDouble3(Qx, Qy, Qz);
        ShapeDouble3 t = ShapeDouble3.Cross(q, v).Scale(2);
        return v.Add(t.Scale(Qw)).Add(ShapeDouble3.Cross(q, t));
    }

    internal ShapeDouble3 Transform(Vector3 local) => Position.Add(Rotate(ShapeDouble3.From(local)));

    internal ShapeDoublePose Compose(Pose local)
    {
        double bx = local.Orientation.X, by = local.Orientation.Y, bz = local.Orientation.Z, bw = local.Orientation.W;
        return new(Transform(local.Position),
            Qw * bx + Qx * bw + Qy * bz - Qz * by,
            Qw * by - Qx * bz + Qy * bw + Qz * bx,
            Qw * bz + Qx * by - Qy * bx + Qz * bw,
            Qw * bw - Qx * bx - Qy * by - Qz * bz);
    }
}

/// <summary>A running min and max over points with per-axis padding.</summary>
internal struct ShapeBoundsAccumulator
{
    double _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

    internal static ShapeBoundsAccumulator Empty => new()
    {
        _minX = double.PositiveInfinity,
        _minY = double.PositiveInfinity,
        _minZ = double.PositiveInfinity,
        _maxX = double.NegativeInfinity,
        _maxY = double.NegativeInfinity,
        _maxZ = double.NegativeInfinity,
    };

    internal void Add(ShapeDouble3 p, double ex, double ey, double ez)
    {
        _minX = Math.Min(_minX, p.X - ex);
        _minY = Math.Min(_minY, p.Y - ey);
        _minZ = Math.Min(_minZ, p.Z - ez);
        _maxX = Math.Max(_maxX, p.X + ex);
        _maxY = Math.Max(_maxY, p.Y + ey);
        _maxZ = Math.Max(_maxZ, p.Z + ez);
    }

    internal readonly (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) ToBounds()
    {
        if (!(_minX <= _maxX && _minY <= _maxY && _minZ <= _maxZ) ||
            !double.IsFinite(_minX) || !double.IsFinite(_minY) || !double.IsFinite(_minZ) ||
            !double.IsFinite(_maxX) || !double.IsFinite(_maxY) || !double.IsFinite(_maxZ))
            throw new ArgumentException("Shape bounds are empty or not finite.");
        return (_minX, _minY, _minZ, _maxX, _maxY, _maxZ);
    }
}
