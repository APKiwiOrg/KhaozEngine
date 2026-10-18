using System;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;
using P = KhaozEngine.Render3D.CoverageEnclosurePredicates;

namespace KhaozEngine.Render3D;

/// <summary>Source-ordered support alternatives, including every interval grid cell and nearest-edge tie.</summary>
internal static class CoverageEnclosureSupport
{
    internal static I Height(V a, V b, V c, V p)
    {
        I result = default;
        bool any = false;
        if (SamePoint(a, p)) return a.Z;
        if (SamePoint(b, p)) return b.Z;
        if (SamePoint(c, p)) return c.Z;
        if (Overlap(a, p)) Join(ref result, ref any, a.Z);
        if (Overlap(b, p)) Join(ref result, ref any, b.Z);
        if (Overlap(c, p)) Join(ref result, ref any, c.Z);
        var u = P.Edge(b, c, p); var v = P.Edge(c, a, p); var w = P.Edge(a, b, p);
        if (u.Zero) Join(ref result, ref any, EdgeHeight(b, c, p));
        if (v.Zero) Join(ref result, ref any, EdgeHeight(c, a, p));
        if (w.Zero) Join(ref result, ref any, EdgeHeight(a, b, p));
        if ((u.Positive || u.Negative) && (v.Positive || v.Negative) && (w.Positive || w.Negative))
        {
            I numerator = F.Add(F.Add(F.Multiply(u.Value, a.Z), F.Multiply(v.Value, b.Z)), F.Multiply(w.Value, c.Z));
            // B30 leaves height's genuine zero-containing area divisor unbounded.
            Join(ref result, ref any, F.Divide(numerator, P.Edge(a, b, c).Value));
        }
        return any ? result : F.Unbounded;
    }

    static I EdgeHeight(V a, V b, V p)
    {
        // Endpoint order is lexicographic in the reference. Join both orders when boxes cannot establish it.
        if (a.X.Max < b.X.Min || a.X.Min == a.X.Max && a.X == b.X && a.Y.Max < b.Y.Min) return OrderedEdgeHeight(a, b, p);
        if (b.X.Max < a.X.Min || a.X.Min == a.X.Max && a.X == b.X && b.Y.Max < a.Y.Min) return OrderedEdgeHeight(b, a, p);
        return F.Hull(OrderedEdgeHeight(a, b, p), OrderedEdgeHeight(b, a, p));
    }

    static I OrderedEdgeHeight(V a, V b, V p)
    {
        I dx = F.Subtract(b.X, a.X), dy = F.Subtract(b.Y, a.Y), x = F.Abs(dx), y = F.Abs(dy);
        I value = default;
        bool any = false;
        if (x.Max >= y.Min) Join(ref value, ref any, Along(p.X, a.X, dx));
        if (x.Min < y.Max) Join(ref value, ref any, Along(p.Y, a.Y, dy));
        return any ? value : F.Unbounded;
        I Along(I q, I start, I delta)
        {
            I t = F.Divide(F.Subtract(q, start), delta);
            return F.Add(F.Multiply(F.Subtract(F.Point(1), t), a.Z), F.Multiply(t, b.Z));
        }
    }

    internal static bool Query(CoverageReferenceSupport support, V point, out I height, out bool certain)
    {
        height = default;
        certain = false;
        bool any = false;
        if (!M.Finite(point) || point.X.Max < support.GridMin.X || point.X.Min > support.GridMax.X
            || point.Y.Max < support.GridMin.Y || point.Y.Min > support.GridMax.Y) return false;
        Span<bool> candidates = stackalloc bool[1024];
        candidates.Clear();
        Cells(point.X, support.GridMin.X, support.GridMax.X, out int x0, out int x1);
        Cells(point.Y, support.GridMin.Y, support.GridMax.Y, out int y0, out int y1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * CoverageReferenceSupport.GridSize + x;
                for (int i = support.CellOffsets[cell]; i < support.CellOffsets[cell + 1]; i++) candidates[support.CellTriangles[i]] = true;
            }
        for (int i = 0; i < support.Triangles.Length; i++)
        {
            if (!candidates[i]) continue;
            var t = support.Triangles[i];
            V a = M.Point(support.Positions[t.A]), b = M.Point(support.Positions[t.B]), c = M.Point(support.Positions[t.C]);
            V hull = Hull(a, b, c);
            if (!Overlap(hull, point) || !P.InsidePossible(a, b, c, point, out bool inside)) continue;
            Join(ref height, ref any, Height(a, b, c, point));
            // An exact inside point is in this triangle's AABB and its binned grid range (B16).
            // If the device can miss that range because of cell arithmetic, retain the miss alternative.
            if (inside && Encloses(hull, point) && GridCertain(support, i, x0, x1, y0, y1))
            { certain = true; break; }
        }
        return any;
    }

    static bool GridCertain(CoverageReferenceSupport support, int triangle, int x0, int x1, int y0, int y1)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * CoverageReferenceSupport.GridSize + x;
                bool found = false;
                for (int i = support.CellOffsets[cell]; i < support.CellOffsets[cell + 1]; i++)
                    if (support.CellTriangles[i] == triangle) { found = true; break; }
                if (!found) return false;
            }
        return true;
    }

    static void Cells(I point, float min, float max, out int first, out int last)
    {
        I value = F.Multiply(F.Divide(F.Subtract(point, F.Point(min)), F.Subtract(F.Point(max), F.Point(min))), F.Point(16));
        first = value.IsFinite ? (int)Math.Clamp(value.Min, 0, 15) : 0;
        last = value.IsFinite ? (int)Math.Clamp(value.Max, 0, 15) : 15;
    }

    internal static void Boundary(CoverageReferenceSupport support, V point, out I height, out I distance)
    {
        Span<I> squared = stackalloc I[256], heights = stackalloc I[256];
        int count = support.BoundarySourceVertices.Length;
        double best = double.PositiveInfinity, lower = double.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            V a = M.Point(support.Positions[support.BoundarySourceVertices[i]]),
                b = M.Point(support.Positions[support.BoundarySourceVertices[(i + 1) % count]]);
            I t = F.Clamp(F.Divide(P.Dot(a, point, a, b), P.Dot(a, b, a, b)), 0, 1);
            I x = F.Subtract(point.X, F.Add(a.X, F.Multiply(t, F.Subtract(b.X, a.X)))),
                y = F.Subtract(point.Y, F.Add(a.Y, F.Multiply(t, F.Subtract(b.Y, a.Y))));
            squared[i] = F.Add(M.Square(x), M.Square(y));
            heights[i] = F.Add(a.Z, F.Multiply(t, F.Subtract(b.Z, a.Z)));
            if (!squared[i].IsFinite || !heights[i].IsFinite) { height = distance = F.Unbounded; return; }
            best = Math.Min(best, squared[i].Max); lower = Math.Min(lower, squared[i].Min);
        }
        height = default;
        bool any = false;
        for (int i = 0; i < count; i++) if (squared[i].Min <= best) Join(ref height, ref any, heights[i]);
        distance = M.Root(new(lower, best));
        if (!any) height = distance = F.Unbounded;
    }

    internal static I Correction(I deficit, float rate) => F.Divide(F.Add(deficit, F.Point(0)), F.Point(rate));

    internal static V Hull(V a, V b, V c) => M.Hull(M.Hull(a, b), c);
    internal static bool Overlap(V a, V b) => Intersects(a.X, b.X) && Intersects(a.Y, b.Y);
    static bool Intersects(I a, I b) => a.Min <= b.Max && b.Min <= a.Max;
    static bool Encloses(V a, V b) => a.X.Min <= b.X.Min && a.X.Max >= b.X.Max && a.Y.Min <= b.Y.Min && a.Y.Max >= b.Y.Max;
    static bool SamePoint(V a, V p) => a.X.Min == a.X.Max && a.Y.Min == a.Y.Max && p.X == a.X && p.Y == a.Y;

    internal static void Join(ref I value, ref bool any, I candidate)
    {
        value = any ? F.Hull(value, candidate) : candidate;
        any = true;
    }
}
