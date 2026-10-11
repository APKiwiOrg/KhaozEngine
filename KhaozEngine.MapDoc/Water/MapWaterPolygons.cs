using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Exact polygon predicates and constructions on the wide rational kernel. Rings are closed implicitly
/// (the last vertex joins the first). Counter-clockwise means a positive shoelace sum
/// <c>sum(x[i] * z[i+1] - x[i+1] * z[i])</c>.</summary>
internal static class MapWaterPolygons
{
    /// <summary>The orientation of <paramref name="p"/> against the directed line from <paramref name="a"/> to
    /// <paramref name="b"/>: positive on the left, the counter-clockwise side.</summary>
    internal static MapWideRational Cross(MapWideXz a, MapWideXz b, MapWideXz p) =>
        b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X)));

    internal static MapWideRational SignedArea(IReadOnlyList<MapWideXz> ring)
    {
        MapWideRational twice = default;
        for (int i = 0; i < ring.Count; i++)
        {
            MapWideXz a = ring[i], b = ring[(i + 1) % ring.Count];
            twice = twice.Add(a.X.Multiply(b.Z)).Subtract(b.X.Multiply(a.Z));
        }
        return twice.Divide(MapWideRational.From(2));
    }

    /// <summary>True when every vertex lies on one line, so the ring encloses no area.</summary>
    internal static bool IsCollinear(IReadOnlyList<MapWideXz> ring)
    {
        int far = -1;
        for (int i = 1; i < ring.Count && far < 0; i++)
            if (ring[i] != ring[0]) far = i;
        if (far < 0) return true;
        for (int i = 1; i < ring.Count; i++)
            if (Cross(ring[0], ring[far], ring[i]).Sign != 0) return false;
        return true;
    }

    /// <summary>True when the ring's edges meet only where consecutive edges share their vertex, with no
    /// zero-length edge and no fold back. Edge pairs come from <see cref="MapWaterBoundsSweep"/>, which charges
    /// <paramref name="budget"/>.</summary>
    internal static bool IsSimple(IReadOnlyList<MapWideXz> ring, MapWaterWorkBudget budget)
    {
        int n = ring.Count;
        if (n < 3) return false;
        for (int i = 0; i < n; i++)
        {
            MapWideXz a = ring[(i + n - 1) % n], b = ring[i], c = ring[(i + 1) % n];
            if (b == c) return false;
            // A straight-back turn overlaps the previous edge.
            if (Cross(a, b, c).Sign == 0 && Dot(b, a, c).Sign > 0) return false;
        }
        if (n == 3) return true;
        var bounds = new Bounds[n];
        for (int i = 0; i < n; i++) bounds[i] = Bounds.Of(ring[i], ring[(i + 1) % n]);
        foreach ((int i, int j) in MapWaterBoundsSweep.Pairs(bounds, touching: true, budget))
        {
            int gap = Math.Abs(i - j);
            if (gap == 1 || gap == n - 1) continue;
            if (SegmentsMeet(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n])) return false;
        }
        return true;
    }

    /// <summary>The OA7 edge rule: an edge of a counter-clockwise ring owns its boundary points when its direction
    /// has negative Z, or zero Z and positive X. That is exactly when the ownership direction (1, epsilon), +X with an
    /// infinitesimal +Z tilt, lies strictly on the edge's interior side.</summary>
    internal static bool EdgeOwns(MapWideXz from, MapWideXz to)
    {
        int dz = to.Z.CompareTo(from.Z);
        return dz < 0 || (dz == 0 && to.X.CompareTo(from.X) > 0);
    }

    /// <summary>Half-open containment in a simple counter-clockwise ring. A point on an edge belongs when that edge
    /// owns it. A vertex belongs when the ownership direction points into the ring's interior angle there: a convex
    /// vertex needs both incident edges to own it, a reflex vertex either one, and at a straight vertex the shared line
    /// decides. Any tiling of pieces therefore owns every point exactly once.</summary>
    internal static bool RingContains(IReadOnlyList<MapWideXz> ring, MapWideXz p)
    {
        int n = ring.Count;
        int winding = 0;
        for (int i = 0; i < n; i++)
        {
            MapWideXz a = ring[i], b = ring[(i + 1) % n];
            if (p == a) return VertexOwns(ring[(i + n - 1) % n], a, b);
            int side = Cross(a, b, p).Sign;
            // An end vertex is decided by the vertex rule when the loop reaches it.
            if (side == 0 && p != b && WithinBox(a, b, p)) return EdgeOwns(a, b);
            if (a.Z.CompareTo(p.Z) <= 0)
            {
                if (b.Z.CompareTo(p.Z) > 0 && side > 0) winding++;
            }
            else if (b.Z.CompareTo(p.Z) <= 0 && side < 0) winding--;
        }
        return winding != 0;
    }

    /// <summary>True when two regions, each given as a list of convex counter-clockwise parts, share positive area.
    /// Shared edges and vertices alone are not overlap. Every part pair charges one check, and a pair whose open
    /// bounds overlap charges its clip as the product of the part sizes.</summary>
    internal static bool IntersectPositively(IReadOnlyList<IReadOnlyList<MapWideXz>> convexA,
        IReadOnlyList<IReadOnlyList<MapWideXz>> convexB, MapWaterWorkBudget budget)
    {
        foreach (IReadOnlyList<MapWideXz> a in convexA)
        {
            Bounds boundsA = Bounds.Of(a);
            foreach (IReadOnlyList<MapWideXz> b in convexB)
            {
                budget.Charge(1);
                if (!boundsA.OverlapsOpen(Bounds.Of(b))) continue;
                budget.Charge((long)a.Count * b.Count);
                if (SignedArea(ClipConvex(a, b)).Sign > 0) return true;
            }
        }
        return false;
    }

    internal static bool IsConvex(IReadOnlyList<MapWideXz> ring)
    {
        for (int i = 0; i < ring.Count; i++)
            if (Cross(ring[i], ring[(i + 1) % ring.Count], ring[(i + 2) % ring.Count]).Sign < 0) return false;
        return true;
    }

    /// <summary>Splits a simple counter-clockwise ring into counter-clockwise triangles by exact ear clipping.
    /// Straight vertices are dropped, so the triangles cover the ring exactly. Null when no ear can be found, which
    /// only a ring that is not simple and counter-clockwise allows. Each step charges the remaining vertex count.</summary>
    internal static IReadOnlyList<MapWideXz[]>? Triangulate(IReadOnlyList<MapWideXz> ring, MapWaterWorkBudget budget)
    {
        var remaining = Enumerable.Range(0, ring.Count).ToList();
        var triangles = new List<MapWideXz[]>();
        int k = 0;
        int sinceClip = 0;
        while (remaining.Count > 3)
        {
            int m = remaining.Count;
            budget.Charge(m);
            k %= m;
            MapWideXz a = ring[remaining[(k + m - 1) % m]], b = ring[remaining[k]], c = ring[remaining[(k + 1) % m]];
            int turn = Cross(a, b, c).Sign;
            if (turn == 0 || (turn > 0 && IsEar(ring, remaining, k, a, b, c)))
            {
                if (turn > 0) triangles.Add(new[] { a, b, c });
                remaining.RemoveAt(k);
                sinceClip = 0;
                continue;
            }
            if (++sinceClip > m) return null;
            k++;
        }
        MapWideXz p = ring[remaining[0]], q = ring[remaining[1]], r = ring[remaining[2]];
        if (Cross(p, q, r).Sign > 0) triangles.Add(new[] { p, q, r });
        return triangles;
    }

    /// <summary>Clips <paramref name="subject"/> to the closed left half-plane of every edge of the convex
    /// counter-clockwise <paramref name="clip"/>. The result may be empty or degenerate.</summary>
    internal static IReadOnlyList<MapWideXz> ClipConvex(IReadOnlyList<MapWideXz> subject, IReadOnlyList<MapWideXz> clip)
    {
        IReadOnlyList<MapWideXz> current = subject;
        for (int i = 0; i < clip.Count && current.Count > 0; i++)
        {
            MapWideXz a = clip[i], b = clip[(i + 1) % clip.Count];
            if (a == b) continue;
            current = ClipHalfPlane(current, p => Cross(a, b, p));
        }
        return current;
    }

    /// <summary>The positive-area part of a convex polygon inside the rectangle from <paramref name="min"/> to
    /// <paramref name="max"/>, or empty. Boundaries carry no area, so the half-open and closed rectangles give the
    /// same part, and a polygon that only touches the rectangle gives nothing.</summary>
    internal static IReadOnlyList<MapWideXz> ClipToRect(IReadOnlyList<MapWideXz> convex, MapWideXz min, MapWideXz max)
    {
        IReadOnlyList<MapWideXz> current = convex;
        current = ClipHalfPlane(current, p => p.X.Subtract(min.X));
        current = ClipHalfPlane(current, p => max.X.Subtract(p.X));
        current = ClipHalfPlane(current, p => p.Z.Subtract(min.Z));
        current = ClipHalfPlane(current, p => max.Z.Subtract(p.Z));
        return current.Count >= 3 && SignedArea(current).Sign != 0 ? current : Array.Empty<MapWideXz>();
    }

    /// <summary>Splits a planar convex polygon at height <paramref name="y"/>. <c>Above</c> is the closure of the part
    /// strictly above, <c>AtOrBelow</c> the closure of the rest, each empty or one convex polygon in the input's vertex
    /// order, so a crossing polygon puts its points at <paramref name="y"/> on both sides. A polygon with no vertex
    /// strictly above is all <c>AtOrBelow</c>, including one lying wholly at <paramref name="y"/>. A polygon with no
    /// vertex strictly below but some above stays whole in <c>Above</c> with <c>AtOrBelow</c> empty. C# reserves
    /// <c>Rest</c> as a tuple element name, hence <c>AtOrBelow</c>.</summary>
    internal static (IReadOnlyList<IReadOnlyList<MapWidePoint>> Above, IReadOnlyList<IReadOnlyList<MapWidePoint>> AtOrBelow) SplitAtHeight(
        IReadOnlyList<MapWidePoint> convex, MapWideRational y)
    {
        bool anyAbove = false, anyBelow = false;
        foreach (MapWidePoint p in convex)
        {
            int s = p.Y.CompareTo(y);
            anyAbove |= s > 0;
            anyBelow |= s < 0;
        }
        var above = new List<IReadOnlyList<MapWidePoint>>();
        var rest = new List<IReadOnlyList<MapWidePoint>>();
        if (anyAbove) above.Add(anyBelow ? ClipAtHeight(convex, y, keepAbove: true) : convex.ToArray());
        if (anyBelow || !anyAbove) rest.Add(anyAbove ? ClipAtHeight(convex, y, keepAbove: false) : convex.ToArray());
        return (above, rest);
    }

    /// <summary>An axis-aligned box in world XZ.</summary>
    internal readonly record struct Bounds(MapWideXz Min, MapWideXz Max)
    {
        public static Bounds Of(MapWideXz a, MapWideXz b) => new(
            new(MapWideRational.Min(a.X, b.X), MapWideRational.Min(a.Z, b.Z)),
            new(MapWideRational.Max(a.X, b.X), MapWideRational.Max(a.Z, b.Z)));

        public static Bounds Of(IReadOnlyList<MapWideXz> ring)
        {
            Bounds result = Of(ring[0], ring[0]);
            for (int i = 1; i < ring.Count; i++) result = result.Include(ring[i]);
            return result;
        }

        public Bounds Include(MapWideXz p) => new(
            new(MapWideRational.Min(Min.X, p.X), MapWideRational.Min(Min.Z, p.Z)),
            new(MapWideRational.Max(Max.X, p.X), MapWideRational.Max(Max.Z, p.Z)));

        public bool OverlapsOpen(Bounds o) => Min.X.CompareTo(o.Max.X) < 0 && o.Min.X.CompareTo(Max.X) < 0 &&
            Min.Z.CompareTo(o.Max.Z) < 0 && o.Min.Z.CompareTo(Max.Z) < 0;

        public bool OverlapsClosed(Bounds o) => Min.X.CompareTo(o.Max.X) <= 0 && o.Min.X.CompareTo(Max.X) <= 0 &&
            Min.Z.CompareTo(o.Max.Z) <= 0 && o.Min.Z.CompareTo(Max.Z) <= 0;

        public bool Contains(MapWideXz p) => Min.X.CompareTo(p.X) <= 0 && p.X.CompareTo(Max.X) <= 0 &&
            Min.Z.CompareTo(p.Z) <= 0 && p.Z.CompareTo(Max.Z) <= 0;
    }

    /// <summary>The vertex rule for <paramref name="vertex"/> between <paramref name="previous"/> and
    /// <paramref name="next"/>, see <see cref="RingContains"/>.</summary>
    static bool VertexOwns(MapWideXz previous, MapWideXz vertex, MapWideXz next)
    {
        bool incoming = EdgeOwns(previous, vertex), outgoing = EdgeOwns(vertex, next);
        int turn = Cross(previous, vertex, next).Sign;
        return turn > 0 ? incoming && outgoing : turn < 0 ? incoming || outgoing : incoming;
    }

    static MapWideRational Dot(MapWideXz origin, MapWideXz a, MapWideXz b) =>
        a.X.Subtract(origin.X).Multiply(b.X.Subtract(origin.X)).Add(a.Z.Subtract(origin.Z).Multiply(b.Z.Subtract(origin.Z)));

    static bool WithinBox(MapWideXz a, MapWideXz b, MapWideXz p) => Bounds.Of(a, b).Contains(p);

    /// <summary>True when the closed segments share any point.</summary>
    static bool SegmentsMeet(MapWideXz a, MapWideXz b, MapWideXz c, MapWideXz d)
    {
        int d1 = Cross(c, d, a).Sign, d2 = Cross(c, d, b).Sign, d3 = Cross(a, b, c).Sign, d4 = Cross(a, b, d).Sign;
        if (d1 * d2 < 0 && d3 * d4 < 0) return true;
        return (d1 == 0 && WithinBox(c, d, a)) || (d2 == 0 && WithinBox(c, d, b)) ||
            (d3 == 0 && WithinBox(a, b, c)) || (d4 == 0 && WithinBox(a, b, d));
    }

    static bool IsEar(IReadOnlyList<MapWideXz> ring, List<int> remaining, int k, MapWideXz a, MapWideXz b, MapWideXz c)
    {
        int m = remaining.Count;
        for (int j = 0; j < m; j++)
        {
            if (j == k || j == (k + m - 1) % m || j == (k + 1) % m) continue;
            MapWideXz p = ring[remaining[j]];
            if (Cross(a, b, p).Sign >= 0 && Cross(b, c, p).Sign >= 0 && Cross(c, a, p).Sign >= 0) return false;
        }
        return true;
    }

    /// <summary>Sutherland-Hodgman against one closed half-plane where <paramref name="side"/> is non-negative.
    /// A crossing is inserted only between strictly opposite vertices, so on-line vertices appear once.</summary>
    static IReadOnlyList<MapWideXz> ClipHalfPlane(IReadOnlyList<MapWideXz> polygon, Func<MapWideXz, MapWideRational> side)
    {
        if (polygon.Count == 0) return polygon;
        var output = new List<MapWideXz>(polygon.Count + 1);
        var values = new MapWideRational[polygon.Count];
        for (int i = 0; i < polygon.Count; i++) values[i] = side(polygon[i]);
        for (int i = 0; i < polygon.Count; i++)
        {
            int j = (i + 1) % polygon.Count;
            MapWideRational si = values[i], sj = values[j];
            if (si.Sign >= 0) output.Add(polygon[i]);
            if (si.Sign * sj.Sign < 0)
            {
                MapWideRational t = si.Divide(si.Subtract(sj));
                MapWideXz p = polygon[i], q = polygon[j];
                output.Add(new(p.X.Add(t.Multiply(q.X.Subtract(p.X))), p.Z.Add(t.Multiply(q.Z.Subtract(p.Z)))));
            }
        }
        return output;
    }

    static MapWidePoint[] ClipAtHeight(IReadOnlyList<MapWidePoint> polygon, MapWideRational y, bool keepAbove)
    {
        var output = new List<MapWidePoint>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            MapWidePoint p = polygon[i], q = polygon[(i + 1) % polygon.Count];
            int sp = p.Y.CompareTo(y), sq = q.Y.CompareTo(y);
            if (keepAbove ? sp >= 0 : sp <= 0) output.Add(p);
            if (sp * sq < 0)
            {
                MapWideRational t = y.Subtract(p.Y).Divide(q.Y.Subtract(p.Y));
                output.Add(new(p.X.Add(t.Multiply(q.X.Subtract(p.X))), y, p.Z.Add(t.Multiply(q.Z.Subtract(p.Z)))));
            }
        }
        return output.ToArray();
    }
}
