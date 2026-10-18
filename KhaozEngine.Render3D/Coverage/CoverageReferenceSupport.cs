using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The selected actual sheet rounded to binary32, with source-ordered inclusive grid candidates.</summary>
internal sealed class CoverageReferenceSupport
{
    internal const int GridSize = 16;
    static readonly float[] CellThresholds = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f];

    internal CoverageReferenceSupport(CoverageSupportSurface support)
    {
        ArgumentNullException.ThrowIfNull(support);
        var f = new CoverageReferenceArithmetic();
        Support = support;
        Positions = support.Positions.ToArray();
        SourceIndices = support.InwardSourceIndices.ToArray();
        Array.Sort(SourceIndices);
        Triangles = new CoverageSourceTriangle[SourceIndices.Length];
        BoundarySourceVertices = support.BoundarySourceVertices.ToArray();
        BoundaryCount = BoundarySourceVertices.Length;
        TriangleCount = Triangles.Length;
        Vector2 min = new Vector2(float.PositiveInfinity), max = new Vector2(float.NegativeInfinity);
        float slope = 0;
        for (int i = 0; i < Triangles.Length; i++)
        {
            var t = Triangles[i] = support.Source.Triangles[SourceIndices[i]];
            Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
            Include(a); Include(b); Include(c);
            Vector3 normal = CoverageReferenceMath.Cross(f, CoverageReferenceMath.Sub3(f, b, a), CoverageReferenceMath.Sub3(f, c, a));
            float denominator = f.Abs(normal.Z);
            float candidate = f.Div(f.Sqrt(f.Add((f.Mul(normal.X, normal.X)), (f.Mul(normal.Y, normal.Y)))), denominator);
            if (!(f.Greater(denominator, 0)) || !f.IsFinite(denominator) || !f.IsFinite(candidate)) slope = float.PositiveInfinity;
            else slope = f.Max(slope, candidate);
        }
        MaxSlope = slope;
        GridMin = min; GridMax = max;
        // The grid defines the query. Bin each binary32 triangle AABB with the same monotone
        // binary32 Cell function used by the point query, including the outer maximum in cell 15.
        // In that cell, retain source order, reject candidates whose inclusive AABB excludes the
        // point, then select the first accepted scalar edge predicate. No accepted candidate means
        // a miss for the caller's boundary path. Part C kernels must reproduce this definition,
        // including its AABB prefilter and exact signs on the binary32 candidate coordinates.
        CellOffsets = new int[GridSize * GridSize + 1];
        for (int t = 0; t < Triangles.Length; t++) VisitCells(f, t, true, null);
        for (int i = 1; i < CellOffsets.Length; i++) CellOffsets[i] += CellOffsets[i - 1];
        CellTriangles = new int[CellOffsets[^1]];
        int[] next = (int[])CellOffsets.Clone();
        for (int t = 0; t < Triangles.Length; t++) VisitCells(f, t, false, next);
        void Include(Vector3 p)
        {
            min = Vector2.Min(min, new Vector2(p.X, p.Y));
            max = Vector2.Max(max, new Vector2(p.X, p.Y));
        }
    }

    internal CoverageSupportSurface Support { get; }
    internal Matrix4x4 SourceToCoverage => Support.SourceToCoverage;
    internal bool InwardOfClosedSolid => Support.InwardOfClosedSolid;
    internal int BoundaryCount { get; }
    internal int TriangleCount { get; }
    internal Vector3[] Positions { get; }
    internal int[] SourceIndices { get; }
    internal CoverageSourceTriangle[] Triangles { get; }
    internal int[] BoundarySourceVertices { get; }
    internal Vector2 GridMin { get; }
    internal Vector2 GridMax { get; }
    internal int[] CellOffsets { get; }
    internal int[] CellTriangles { get; }
    internal float MaxSlope { get; }

    [Binary32Kernel]
    internal bool TryHeight(CoverageReferenceArithmetic f, Vector2 point, out float height)
    {
        int ignored1;
        return TryHeight(f, point, out height, out ignored1);
    }
    [Binary32Kernel]
    internal bool TryHeight(CoverageReferenceArithmetic f, Vector2 point, out float height, out int sourceTriangle)
    {
        height = 0;
        sourceTriangle = -1;
        if (!f.IsFinite(point.X) || !f.IsFinite(point.Y) || f.Less(point.X, GridMin.X) || f.Greater(point.X, GridMax.X)
            || f.Less(point.Y, GridMin.Y) || f.Greater(point.Y, GridMax.Y)) return false;
        int cell = Cell(f, point.Y, GridMin.Y, GridMax.Y) * GridSize + Cell(f, point.X, GridMin.X, GridMax.X);
        Vector3 p = new Vector3(point, 0);
        for (int i = CellOffsets[cell]; i < CellOffsets[cell + 1]; i++)
        {
            Vector2 min;
            Vector2 max;
            var t = Triangles[CellTriangles[i]];
            Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
            Bounds(f, a, b, c, out min, out max);
            if (f.Less(point.X, min.X) || f.Greater(point.X, max.X) || f.Less(point.Y, min.Y) || f.Greater(point.Y, max.Y)) continue;
            CoverageReferenceScalar area = f.Edge(a, b, c), u = f.Edge(b, c, p),
                v = f.Edge(c, a, p), w = f.Edge(a, b, p);
            if (!f.IsFinite(area.Value) || area.Sign == 0 || !f.IsFinite(u.Value) || !f.IsFinite(v.Value) || !f.IsFinite(w.Value)) return false;
            if (u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) continue;
            height = CoverageReferenceMath.Height(f, a, b, c, point);
            if (!f.IsFinite(height)) return false;
            sourceTriangle = SourceIndices[CellTriangles[i]];
            return true;
        }
        return false;
    }

    [Binary32Kernel]
    internal bool TryBoundary(CoverageReferenceArithmetic f, Vector2 point, out float height, out float distance)
    {
        height = distance = 0;
        if (!f.IsFinite(point.X) || !f.IsFinite(point.Y)) return false;
        float best = float.PositiveInfinity;
        for (int edge = 0; edge < BoundaryCount; edge++)
        {
            Vector3 a = Positions[BoundarySourceVertices[edge]], b = Positions[BoundarySourceVertices[(edge + 1) % BoundaryCount]];
            Vector2 delta = new Vector2(f.Sub(b.X, a.X), f.Sub(b.Y, a.Y));
            CoverageReferenceScalar length = f.Dot(a, b, a, b),
                dot = f.Dot(a, new Vector3(point, 0), a, b);
            if (length.Sign <= 0 || !f.IsFinite(length.Value) || !f.IsFinite(dot.Value)) return false;
            float projection = f.Div(dot.Value, length.Value);
            if (!f.IsFinite(projection)) return false;
            float t = f.Clamp(projection, 0, 1);
            Vector2 nearest = new Vector2(f.Add(a.X, (f.Mul(t, delta.X))), f.Add(a.Y, (f.Mul(t, delta.Y))));
            Vector2 remaining = new Vector2(f.Sub(point.X, nearest.X), f.Sub(point.Y, nearest.Y));
            float squared = CoverageReferenceMath.Dot2(f, remaining, remaining);
            float candidate = f.Add(a.Z, (f.Mul(t, (f.Sub(b.Z, a.Z)))));
            if (!f.IsFinite(squared) || !f.IsFinite(candidate)) return false;
            if (f.Less(squared, best)) { best = squared; height = candidate; }
        }
        distance = f.Sqrt(best);
        return f.IsFinite(distance);
    }

    /// <summary>Analytic overlay vertices in source order. Exact signs on binary32 inputs choose the same inside,
    /// collinear, endpoint and crossing branches as CoverageFaceOverlay. Crossing parameters use B30's scaled RN32 sides.</summary>
    internal bool TryOverlayMaximum(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, out float maximum)
    {
        if (host.Length != 3) { maximum = float.NegativeInfinity; return false; }
        Span<Vector3> sheet = stackalloc Vector3[3];
        Span<CoverageReferenceScalar> hostInSheet = stackalloc CoverageReferenceScalar[9], sheetInHost = stackalloc CoverageReferenceScalar[9];
        return OverlayMaximum(f, host, sheet, hostInSheet, sheetInHost, out maximum);
    }

    [Binary32Kernel]
    bool OverlayMaximum(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, Span<Vector3> sheet,
        Span<CoverageReferenceScalar> hostInSheet, Span<CoverageReferenceScalar> sheetInHost, out float maximum)
    {
        maximum = float.NegativeInfinity;
        for (int i = 0; i < 3; i++) if (!CoverageReferenceMath.IsFinite3(f, host[i])) return false;
        CoverageReferenceScalar hostArea = f.Edge(host[0], host[1], host[2]);
        if (hostArea.Sign == 0 || !f.IsFinite(hostArea.Value)) return false;
        for (int t = 0; t < TriangleCount; t++)
        {
            var triangle = Triangles[t];
            sheet[0] = Positions[triangle.A]; sheet[1] = Positions[triangle.B]; sheet[2] = Positions[triangle.C];
            if (!OverlayPair(f, host, sheet, hostArea, hostInSheet, sheetInHost, ref maximum)) return false;
        }
        return true;
    }

    [Binary32Kernel]
    static bool OverlayPair(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, CoverageReferenceScalar hostArea,
        Span<CoverageReferenceScalar> hostInSheet, Span<CoverageReferenceScalar> sheetInHost, ref float maximum)
    {
        for (int i = 0; i < 3; i++) if (!CoverageReferenceMath.IsFinite3(f, sheet[i])) return false;
        CoverageReferenceScalar sheetArea = f.Edge(sheet[0], sheet[1], sheet[2]);
        if (sheetArea.Sign == 0 || !f.IsFinite(sheetArea.Value)) return false;
        for (int i = 0; i < 3; i++)
        {
            bool inside;
            if (!OverlayInside(f, sheet, host[i], sheetArea, hostInSheet.Slice(i * 3, 3), out inside)) return false;
            if (inside && !OverlayAccumulate(f, host, sheet, host[i], ref maximum)) return false;
            if (!OverlayInside(f, host, sheet[i], hostArea, sheetInHost.Slice(i * 3, 3), out inside)) return false;
            if (inside && !OverlayAccumulate(f, host, sheet, sheet[i], ref maximum)) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                int nextI = (i + 1) % 3, nextJ = (j + 1) % 3;
                CoverageReferenceScalar ca = hostInSheet[i * 3 + j], cb = hostInSheet[nextI * 3 + j],
                    ac = sheetInHost[j * 3 + i], ad = sheetInHost[nextJ * 3 + i];
                if (ca.Sign * cb.Sign > 0 || ac.Sign * ad.Sign > 0) continue;
                // Collinear overlap extrema were included as corners. Exact endpoint touches need no division.
                if (ca.Sign == 0 && cb.Sign == 0) continue;
                Vector3 a = host[i], b = host[nextI], p;
                if (ca.Sign == 0) p = a;
                else if (cb.Sign == 0) p = b;
                else
                {
                    float quotient = f.CrossingParameter(sheet[j], sheet[nextJ], a, b);
                    if (!f.IsFinite(quotient)) return false;
                    float u = f.Clamp(quotient, 0, 1);
                    p = new Vector3(f.Add(a.X, (f.Mul(u, (f.Sub(b.X, a.X))))), f.Add(a.Y, (f.Mul(u, (f.Sub(b.Y, a.Y))))), f.Add(a.Z, (f.Mul(u, (f.Sub(b.Z, a.Z))))));
                }
                if (!CoverageReferenceMath.IsFinite3(f, p) || !OverlayAccumulate(f, host, sheet, p, ref maximum)) return false;
            }
        return true;
    }

    [Binary32Kernel]
    static bool OverlayInside(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> triangle, Vector3 p, CoverageReferenceScalar area,
        Span<CoverageReferenceScalar> sides, out bool inside)
    {
        inside = true;
        for (int i = 0; i < 3; i++)
        {
            CoverageReferenceScalar side = sides[i] = f.Edge(triangle[i], triangle[(i + 1) % 3], p);
            if (!f.IsFinite(side.Value)) return false;
            if (side.Sign * area.Sign < 0) inside = false;
        }
        return true;
    }

    [Binary32Kernel]
    static bool OverlayAccumulate(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, Vector3 p, ref float maximum)
    {
        Vector2 xy = new Vector2(p.X, p.Y);
        float h = CoverageReferenceMath.Height(f, host[0], host[1], host[2], xy),
            s = CoverageReferenceMath.Height(f, sheet[0], sheet[1], sheet[2], xy), difference = f.Sub(h, s);
        if (!f.IsFinite(h) || !f.IsFinite(s) || !f.IsFinite(difference)) return false;
        maximum = f.Max(maximum, difference);
        return true;
    }

    void VisitCells(CoverageReferenceArithmetic f, int triangle, bool counting, int[]? next)
    {
        Vector2 min;
        Vector2 max;
        var t = Triangles[triangle];
        Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
        Bounds(f, a, b, c, out min, out max);
        int x0 = Cell(f, min.X, GridMin.X, GridMax.X), x1 = Cell(f, max.X, GridMin.X, GridMax.X),
            y0 = Cell(f, min.Y, GridMin.Y, GridMax.Y), y1 = Cell(f, max.Y, GridMin.Y, GridMax.Y);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * GridSize + x;
                if (counting) CellOffsets[cell + 1]++;
                else CellTriangles[next![cell]++] = triangle;
            }
    }

    [Binary32Kernel]
    static void Bounds(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(f.Min(a.X, f.Min(b.X, c.X)), f.Min(a.Y, f.Min(b.Y, c.Y)));
        max = new Vector2(f.Max(a.X, f.Max(b.X, c.X)), f.Max(a.Y, f.Max(b.Y, c.Y)));
    }

    // The compare count has the saturating truncation's clamped result, including NaN and infinities.
    [Binary32Kernel]
    internal static int Cell(CoverageReferenceArithmetic f, float value, float min, float max)
    {
        float x = f.Mul(f.Div(f.Sub(value, min), f.Sub(max, min)), 16f);
        int cell = 0;
        foreach (float threshold in CellThresholds) if (f.GreaterOrEqual(x, threshold)) cell++;
        return cell;
    }
}
