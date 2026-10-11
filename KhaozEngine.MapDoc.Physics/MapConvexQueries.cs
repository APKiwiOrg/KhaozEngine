using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Scalar double GJK over point sets: the distance between two convex hulls and a ray cast against one.
/// Support ties resolve to the lowest point index and every loop runs in a fixed order, so equal inputs give equal
/// answers on every platform. A set of one or two points is a point or a segment.</summary>
internal static class MapConvexQueries
{
    const int MaxIterations = 96;

    // Distance stops once the gap between its upper and lower bound is this fraction of the squared distance.
    const double DistanceTolerance = 1e-12;

    // A ray cast converges once the simplex distance is this fraction of its farthest vertex.
    const double CastTolerance = 1e-9, LooseCastTolerance = 1e-6;

    /// <summary>The distance between conv(<paramref name="a"/>) and conv(<paramref name="b"/>), zero when they
    /// overlap.</summary>
    internal static double Distance(ReadOnlySpan<MapDouble3> a, ReadOnlySpan<MapDouble3> b)
    {
        if (a.IsEmpty || b.IsEmpty) throw new ArgumentException("Both point sets need at least one point.");
        Span<MapDouble3> w = stackalloc MapDouble3[4];
        Span<int> ia = stackalloc int[4];
        Span<int> ib = stackalloc int[4];
        int n = 1;
        w[0] = a[0].Subtract(b[0]);
        ia[0] = 0;
        ib[0] = 0;
        MapDouble3 v = w[0];
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            double vv = Dot(v, v);
            if (vv == 0d) return 0d;
            int sa = Support(a, v.Scale(-1d)), sb = Support(b, v);
            MapDouble3 p = a[sa].Subtract(b[sb]);
            if (vv - Dot(v, p) <= DistanceTolerance * vv) break;
            bool repeated = false;
            for (int i = 0; i < n; i++) repeated |= ia[i] == sa && ib[i] == sb;
            if (repeated) break;
            w[n] = p;
            ia[n] = sa;
            ib[n] = sb;
            n++;
            MapDouble3 next = Closest(w, ia, ib, ref n);
            if (n == 4) return 0d;
            if (Dot(next, next) >= vv) break;
            v = next;
        }
        return Math.Sqrt(Dot(v, v));
    }

    /// <summary>Casts <paramref name="start"/> + t <paramref name="direction"/> for t in [0,
    /// <paramref name="maxT"/>] against conv(<paramref name="points"/>). A hit reports its parameter and the outward
    /// normal, which is zero when the start already lies in the hull.</summary>
    internal static bool Raycast(ReadOnlySpan<MapDouble3> points, MapDouble3 start, MapDouble3 direction, double maxT,
        out double t, out MapDouble3 normal)
    {
        if (points.IsEmpty) throw new ArgumentException("A hull needs at least one point.", nameof(points));
        t = 0d;
        normal = default;
        MapDouble3 x = start;
        Span<MapDouble3> w = stackalloc MapDouble3[4];
        Span<int> index = stackalloc int[4];
        Span<int> ignored = stackalloc int[4];
        int n = 0;
        MapDouble3 v = x.Subtract(points[0]);
        double vv;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            vv = Dot(v, v);
            if (vv == 0d || (n > 0 && vv <= CastTolerance * CastTolerance * Farthest(w, n))) return true;
            int sp = Support(points, v);
            MapDouble3 p = x.Subtract(points[sp]);
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
    internal static MapDouble3[] ClipToSlab(ReadOnlySpan<MapDouble3> points, double minY, double maxY)
    {
        var clipped = new List<MapDouble3>();
        foreach (MapDouble3 p in points)
            if (p.Y >= minY && p.Y <= maxY) clipped.Add(p);
        for (int i = 0; i < points.Length; i++)
            for (int j = i + 1; j < points.Length; j++)
            {
                Crossing(points[i], points[j], minY, clipped);
                Crossing(points[i], points[j], maxY, clipped);
            }
        return clipped.ToArray();
    }

    static void Crossing(MapDouble3 a, MapDouble3 b, double y, List<MapDouble3> output)
    {
        if (!((a.Y < y && b.Y > y) || (a.Y > y && b.Y < y))) return;
        double s = (y - a.Y) / (b.Y - a.Y);
        output.Add(new MapDouble3(a.X + s * (b.X - a.X), y, a.Z + s * (b.Z - a.Z)));
    }

    internal static double Dot(MapDouble3 a, MapDouble3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>The point of triangle abc nearest <paramref name="point"/>.</summary>
    internal static MapDouble3 ClosestOnTriangle(MapDouble3 point, MapDouble3 a, MapDouble3 b, MapDouble3 c) =>
        OnTriangle(a.Subtract(point), b.Subtract(point), c.Subtract(point), out _).Add(point);

    static int Support(ReadOnlySpan<MapDouble3> points, MapDouble3 direction)
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

    static double Farthest(ReadOnlySpan<MapDouble3> w, int n)
    {
        double farthest = 0d;
        for (int i = 0; i < n; i++) farthest = Math.Max(farthest, Dot(w[i], w[i]));
        return farthest;
    }

    // The point of the simplex nearest the origin. The simplex keeps only the vertices that support it, in their
    // original order, with their index pairs. Four vertices remain only when the origin lies inside.
    static MapDouble3 Closest(Span<MapDouble3> w, Span<int> ia, Span<int> ib, ref int n)
    {
        int mask;
        MapDouble3 closest;
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

    static MapDouble3 OnSegment(MapDouble3 a, MapDouble3 b, out int mask)
    {
        MapDouble3 ab = b.Subtract(a);
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
    static MapDouble3 OnTriangle(MapDouble3 a, MapDouble3 b, MapDouble3 c, out int mask)
    {
        MapDouble3 ab = b.Subtract(a), ac = c.Subtract(a);
        MapDouble3 ap = a.Scale(-1d);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0d && d2 <= 0d)
        {
            mask = 0b001;
            return a;
        }
        MapDouble3 bp = b.Scale(-1d);
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
        MapDouble3 cp = c.Scale(-1d);
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

    static MapDouble3 OnTriangleEdges(MapDouble3 a, MapDouble3 b, MapDouble3 c, out int mask)
    {
        MapDouble3 best = OnSegment(a, b, out int m);
        mask = m;
        MapDouble3 candidate = OnSegment(b, c, out m);
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
    static MapDouble3 OnTetrahedron(MapDouble3 a, MapDouble3 b, MapDouble3 c, MapDouble3 d, out int mask)
    {
        mask = 0b1111;
        MapDouble3 best = default;
        double bestDistance = double.PositiveInfinity;
        Face(a, b, c, d, 0, 1, 2, ref best, ref bestDistance, ref mask);
        Face(a, c, d, b, 0, 2, 3, ref best, ref bestDistance, ref mask);
        Face(a, d, b, c, 0, 3, 1, ref best, ref bestDistance, ref mask);
        Face(b, d, c, a, 1, 3, 2, ref best, ref bestDistance, ref mask);
        return best;
    }

    static void Face(MapDouble3 p, MapDouble3 q, MapDouble3 r, MapDouble3 opposite, int ip, int iq, int ir,
        ref MapDouble3 best, ref double bestDistance, ref int mask)
    {
        MapDouble3 normal = MapDouble3.Cross(q.Subtract(p), r.Subtract(p));
        double origin = -Dot(p, normal), far = Dot(opposite.Subtract(p), normal);
        if (far != 0d && origin * far >= 0d) return;
        MapDouble3 closest = OnTriangle(p, q, r, out int faceMask);
        double distance = Dot(closest, closest);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = closest;
        mask = ((faceMask & 1) << ip) | (((faceMask >> 1) & 1) << iq) | (((faceMask >> 2) & 1) << ir);
    }
}
