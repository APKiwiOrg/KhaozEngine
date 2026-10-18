using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The selected actual sheet rounded to binary32, with source-ordered inclusive grid candidates.</summary>
internal sealed class CoverageReferenceSupport
{
    internal const int GridSize = 16;

    internal CoverageReferenceSupport(CoverageSupportSurface support)
    {
        ArgumentNullException.ThrowIfNull(support);
        Support = support;
        Positions = support.Positions.ToArray();
        SourceIndices = support.InwardSourceIndices.ToArray();
        Array.Sort(SourceIndices);
        Triangles = new CoverageSourceTriangle[SourceIndices.Length];
        BoundarySourceVertices = support.BoundarySourceVertices.ToArray();
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        float slope = 0;
        for (int i = 0; i < Triangles.Length; i++)
        {
            var t = Triangles[i] = support.Source.Triangles[SourceIndices[i]];
            Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
            Include(a); Include(b); Include(c);
            Vector3 normal = CoverageReferenceMath.Cross(b - a, c - a);
            float denominator = MathF.Abs(normal.Z);
            float candidate = MathF.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)) / denominator;
            if (!(denominator > 0) || !float.IsFinite(denominator) || !float.IsFinite(candidate)) slope = float.PositiveInfinity;
            else slope = MathF.Max(slope, candidate);
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
        for (int t = 0; t < Triangles.Length; t++) VisitCells(t, true, null);
        for (int i = 1; i < CellOffsets.Length; i++) CellOffsets[i] += CellOffsets[i - 1];
        CellTriangles = new int[CellOffsets[^1]];
        int[] next = (int[])CellOffsets.Clone();
        for (int t = 0; t < Triangles.Length; t++) VisitCells(t, false, next);
        void Include(Vector3 p)
        {
            min = Vector2.Min(min, new(p.X, p.Y));
            max = Vector2.Max(max, new(p.X, p.Y));
        }
    }

    internal CoverageSupportSurface Support { get; }
    internal Matrix4x4 SourceToCoverage => Support.SourceToCoverage;
    internal bool InwardOfClosedSolid => Support.InwardOfClosedSolid;
    internal Vector3[] Positions { get; }
    internal int[] SourceIndices { get; }
    internal CoverageSourceTriangle[] Triangles { get; }
    internal int[] BoundarySourceVertices { get; }
    internal Vector2 GridMin { get; }
    internal Vector2 GridMax { get; }
    internal int[] CellOffsets { get; }
    internal int[] CellTriangles { get; }
    internal float MaxSlope { get; }

    internal bool TryHeight(Vector2 point, out float height) => TryHeight(point, out height, out _);

    internal bool TryHeight(Vector2 point, out float height, out int sourceTriangle)
    {
        height = 0;
        sourceTriangle = -1;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X < GridMin.X || point.X > GridMax.X
            || point.Y < GridMin.Y || point.Y > GridMax.Y) return false;
        int cell = Cell(point.Y, GridMin.Y, GridMax.Y) * GridSize + Cell(point.X, GridMin.X, GridMax.X);
        Vector3 p = new(point, 0);
        for (int i = CellOffsets[cell]; i < CellOffsets[cell + 1]; i++)
        {
            var t = Triangles[CellTriangles[i]];
            Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
            Bounds(a, b, c, out Vector2 min, out Vector2 max);
            if (point.X < min.X || point.X > max.X || point.Y < min.Y || point.Y > max.Y) continue;
            CoverageReferenceScalar area = CoverageReferencePredicates.Edge(a, b, c), u = CoverageReferencePredicates.Edge(b, c, p),
                v = CoverageReferencePredicates.Edge(c, a, p), w = CoverageReferencePredicates.Edge(a, b, p);
            if (!area.IsFinite || area.Sign == 0 || !u.IsFinite || !v.IsFinite || !w.IsFinite) return false;
            if (u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) continue;
            height = CoverageReferenceMath.Height(a, b, c, point);
            if (!float.IsFinite(height)) return false;
            sourceTriangle = SourceIndices[CellTriangles[i]];
            return true;
        }
        return false;
    }

    internal bool TryBoundary(Vector2 point, out float height, out float distance)
    {
        height = distance = 0;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        float best = float.PositiveInfinity;
        for (int edge = 0; edge < BoundarySourceVertices.Length; edge++)
        {
            Vector3 a = Positions[BoundarySourceVertices[edge]], b = Positions[BoundarySourceVertices[(edge + 1) % BoundarySourceVertices.Length]];
            Vector2 delta = new(b.X - a.X, b.Y - a.Y);
            CoverageReferenceScalar length = CoverageReferencePredicates.Dot(a, b, a, b),
                dot = CoverageReferencePredicates.Dot(a, new(point, 0), a, b);
            if (length.Sign <= 0 || !length.IsFinite || !dot.IsFinite) return false;
            float projection = dot.Value / length.Value;
            if (!float.IsFinite(projection)) return false;
            float t = Math.Clamp(projection, 0, 1);
            Vector2 nearest = new(a.X + (t * delta.X), a.Y + (t * delta.Y));
            Vector2 remaining = new(point.X - nearest.X, point.Y - nearest.Y);
            float squared = CoverageReferenceMath.Dot2(remaining, remaining);
            float candidate = a.Z + (t * (b.Z - a.Z));
            if (!float.IsFinite(squared) || !float.IsFinite(candidate)) return false;
            if (squared < best) { best = squared; height = candidate; }
        }
        distance = MathF.Sqrt(best);
        return float.IsFinite(distance);
    }

    /// <summary>Analytic overlay vertices in source order. Exact signs on binary32 inputs choose the same inside,
    /// collinear, endpoint and crossing branches as CoverageFaceOverlay. Crossing parameters use B30's scaled RN32 sides.</summary>
    internal bool TryOverlayMaximum(ReadOnlySpan<Vector3> host, out float maximum)
    {
        maximum = float.NegativeInfinity;
        if (host.Length != 3) return false;
        for (int i = 0; i < 3; i++) if (!CoverageRefinement.Finite(host[i])) return false;
        CoverageReferenceScalar hostArea = CoverageReferencePredicates.Edge(host[0], host[1], host[2]);
        if (hostArea.Sign == 0 || !hostArea.IsFinite) return false;
        Span<Vector3> sheet = stackalloc Vector3[3];
        for (int t = 0; t < Triangles.Length; t++)
        {
            var triangle = Triangles[t];
            sheet[0] = Positions[triangle.A]; sheet[1] = Positions[triangle.B]; sheet[2] = Positions[triangle.C];
            if (!OverlayPair(host, sheet, hostArea, ref maximum)) return false;
        }
        return true;
    }

    static bool OverlayPair(ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, CoverageReferenceScalar hostArea, ref float maximum)
    {
        for (int i = 0; i < 3; i++) if (!CoverageRefinement.Finite(sheet[i])) return false;
        CoverageReferenceScalar sheetArea = CoverageReferencePredicates.Edge(sheet[0], sheet[1], sheet[2]);
        if (sheetArea.Sign == 0 || !sheetArea.IsFinite) return false;
        Span<CoverageReferenceScalar> hostInSheet = stackalloc CoverageReferenceScalar[9], sheetInHost = stackalloc CoverageReferenceScalar[9];
        for (int i = 0; i < 3; i++)
        {
            if (!OverlayInside(sheet, host[i], sheetArea, hostInSheet.Slice(i * 3, 3), out bool inside)) return false;
            if (inside && !OverlayAccumulate(host, sheet, host[i], ref maximum)) return false;
            if (!OverlayInside(host, sheet[i], hostArea, sheetInHost.Slice(i * 3, 3), out inside)) return false;
            if (inside && !OverlayAccumulate(host, sheet, sheet[i], ref maximum)) return false;
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
                    float quotient = CoverageReferencePredicates.CrossingParameter(sheet[j], sheet[nextJ], a, b);
                    if (!float.IsFinite(quotient)) return false;
                    float u = Math.Clamp(quotient, 0, 1);
                    p = new(a.X + (u * (b.X - a.X)), a.Y + (u * (b.Y - a.Y)), a.Z + (u * (b.Z - a.Z)));
                }
                if (!CoverageRefinement.Finite(p) || !OverlayAccumulate(host, sheet, p, ref maximum)) return false;
            }
        return true;
    }

    static bool OverlayInside(ReadOnlySpan<Vector3> triangle, Vector3 p, CoverageReferenceScalar area,
        Span<CoverageReferenceScalar> sides, out bool inside)
    {
        inside = true;
        for (int i = 0; i < 3; i++)
        {
            CoverageReferenceScalar side = sides[i] = CoverageReferencePredicates.Edge(triangle[i], triangle[(i + 1) % 3], p);
            if (!side.IsFinite) return false;
            if (side.Sign * area.Sign < 0) inside = false;
        }
        return true;
    }

    static bool OverlayAccumulate(ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, Vector3 p, ref float maximum)
    {
        Vector2 xy = new(p.X, p.Y);
        float h = CoverageReferenceMath.Height(host[0], host[1], host[2], xy),
            s = CoverageReferenceMath.Height(sheet[0], sheet[1], sheet[2], xy), difference = h - s;
        if (!float.IsFinite(h) || !float.IsFinite(s) || !float.IsFinite(difference)) return false;
        maximum = MathF.Max(maximum, difference);
        return true;
    }

    void VisitCells(int triangle, bool counting, int[]? next)
    {
        var t = Triangles[triangle];
        Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
        Bounds(a, b, c, out Vector2 min, out Vector2 max);
        int x0 = Cell(min.X, GridMin.X, GridMax.X), x1 = Cell(max.X, GridMin.X, GridMax.X),
            y0 = Cell(min.Y, GridMin.Y, GridMax.Y), y1 = Cell(max.Y, GridMin.Y, GridMax.Y);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * GridSize + x;
                if (counting) CellOffsets[cell + 1]++;
                else CellTriangles[next![cell]++] = triangle;
            }
    }

    static void Bounds(Vector3 a, Vector3 b, Vector3 c, out Vector2 min, out Vector2 max)
    {
        min = new(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
        max = new(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
    }

    // Subtract, divide, multiply in binary32, truncate to an integer, then clamp.
    static int Cell(float value, float min, float max) => Math.Clamp((int)(((value - min) / (max - min)) * GridSize), 0, GridSize - 1);
}
