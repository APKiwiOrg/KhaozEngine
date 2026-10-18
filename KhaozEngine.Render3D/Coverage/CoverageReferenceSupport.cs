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
            float area = CoverageReferenceMath.Edge(a, b, c), u = CoverageReferenceMath.Edge(b, c, p),
                v = CoverageReferenceMath.Edge(c, a, p), w = CoverageReferenceMath.Edge(a, b, p);
            if (!float.IsFinite(area) || area == 0 || !float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(w)) return false;
            if (area > 0 ? u < 0 || v < 0 || w < 0 : u > 0 || v > 0 || w > 0) continue;
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
            Vector2 delta = new(b.X - a.X, b.Y - a.Y), fromA = new(point.X - a.X, point.Y - a.Y);
            float denominator = CoverageReferenceMath.Dot2(delta, delta);
            float numerator = CoverageReferenceMath.Dot2(fromA, delta);
            if (!(denominator > 0) || !float.IsFinite(denominator) || !float.IsFinite(numerator)) return false;
            float projection = numerator / denominator;
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

    void VisitCells(int triangle, bool counting, int[]? next)
    {
        var t = Triangles[triangle];
        Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
        int x0 = Cell(MathF.Min(a.X, MathF.Min(b.X, c.X)), GridMin.X, GridMax.X),
            x1 = Cell(MathF.Max(a.X, MathF.Max(b.X, c.X)), GridMin.X, GridMax.X),
            y0 = Cell(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), GridMin.Y, GridMax.Y),
            y1 = Cell(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), GridMin.Y, GridMax.Y);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * GridSize + x;
                if (counting) CellOffsets[cell + 1]++;
                else CellTriangles[next![cell]++] = triangle;
            }
    }

    // Grid addressing is CPU-only acceleration, independent of the reproduced float query expression graph.
    static int Cell(float value, float min, float max) => Math.Clamp((int)(((double)value - min) / ((double)max - min) * GridSize), 0, GridSize - 1);
}
