using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KhaozEngine.Render3D;

/// <summary>A directed edge of the actual inward sheet, in coverage coordinates.</summary>
public readonly record struct CoverageBoundaryEdge(Vector3 A, Vector3 B);

/// <summary>An immutable displayed equipment solid and its certified single-valued inward support.</summary>
public sealed class CoverageSupportSurface
{
    public const int MaxTriangles = 1_024;
    public const int MaxBoundaryEdges = 256;
    readonly Vector3[] positions;
    readonly int[] selected;
    readonly CoverageBoundaryEdge[] boundary;

    CoverageSupportSurface(CoverageSourceTopology source, Matrix4x4 coverageLocal, Vector3[] positions,
        int[] selected, CoverageBoundaryEdge[] boundary)
    {
        Source = source;
        CoverageLocal = coverageLocal;
        this.positions = positions;
        this.selected = selected;
        this.boundary = boundary;
        InwardTriangles = Array.AsReadOnly(selected.Select(index => source.Triangles[index]).ToArray());
        Boundary = Array.AsReadOnly(boundary);
        SolidBvh = new CoverageBvh(source.Triangles.Select(t => Bounds(positions[t.A], positions[t.B], positions[t.C], false)).ToArray());
        ProjectedBvh = new CoverageBvh(InwardTriangles.Select(t => Bounds(positions[t.A], positions[t.B], positions[t.C], true)).ToArray());
        EdgeBvh = new CoverageBvh(boundary.Select(e => Bounds(e.A, e.B, e.B, true)).ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes("KhaozEngine.CoverageSupport.v1\0" + source.SourceSha256));
        Add(positions); Add(source.Vertices.ToArray()); Add(source.Triangles.ToArray());
        Add(source.Occurrences.ToArray()); Add(source.Materials.ToArray()); Add(selected); Add(new[] { coverageLocal });
        void Add<T>(T[] values) where T : unmanaged
        {
            hash.AppendData(BitConverter.GetBytes(values.Length));
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        Identity = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public CoverageSourceTopology Source { get; }
    public Matrix4x4 CoverageLocal { get; }
    public string Identity { get; }
    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<int> InwardSourceIndices => selected;
    public IReadOnlyList<CoverageSourceTriangle> InwardTriangles { get; }
    public IReadOnlyList<CoverageBoundaryEdge> Boundary { get; }
    public CoverageBvh SolidBvh { get; }
    public CoverageBvh ProjectedBvh { get; }
    public CoverageBvh EdgeBvh { get; }

    public static CoverageSupportSurface Compile(CoverageSourceTopology source, ReadOnlySpan<CoverageTriangleRange> inwardFaces,
        in Matrix4x4 coverageLocal)
    {
        ArgumentNullException.ThrowIfNull(source);
        Matrix4x4 inverse = CoverageSupportValidation.InvertFrame(source, coverageLocal);
        int[] selected = source.Select(inwardFaces);
        if (selected.Length is 0 or > MaxTriangles) throw source.Error($"Support requires 1 to {MaxTriangles} triangles.");
        foreach (int index in selected)
        {
            CoverageSourceTriangle triangle = source.Triangles[index];
            CoverageSourceOccurrence occurrence = source.Occurrences.Single(o => o.Mesh == triangle.Mesh && o.Node == triangle.Node);
            CoverageSupportValidation.InvertFrame(source, occurrence.Transform);
            if (source.Materials.Single(m => m.Slice == triangle.MaterialSlice).AlphaMode != 0)
                throw source.Error("Inward support requires opaque displayed triangles.");
        }
        Vector3[] positions = source.Positions.ToArray();
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = Vector3.Transform(positions[i], inverse);
            if (!float.IsFinite(positions[i].X) || !float.IsFinite(positions[i].Y) || !float.IsFinite(positions[i].Z))
                throw source.Error("Coverage transform produced a nonfinite position.");
        }
        CoverageBoundaryEdge[] boundary = CoverageSupportValidation.Validate(source, positions, selected);
        return new CoverageSupportSurface(source, coverageLocal, positions, selected, boundary);
    }

    /// <summary>Query the actual projected triangles. Points outside their complete domain return false.</summary>
    public bool TryGetHeight(Vector2 point, out float height)
    {
        height = 0;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && HeightAt(0, point, ref height);
    }

    bool HeightAt(int index, Vector2 point, ref float height)
    {
        CoverageBvhNode node = ProjectedBvh.Nodes[index];
        if (point.X < node.Min.X || point.X > node.Max.X || point.Y < node.Min.Y || point.Y > node.Max.Y) return false;
        if (node.Item < 0) return HeightAt(node.Left, point, ref height) || HeightAt(node.Right, point, ref height);
        CoverageSourceTriangle triangle = InwardTriangles[node.Item];
        Vector3 a = positions[triangle.A], b = positions[triangle.B], c = positions[triangle.C];
        double area = CoverageSupportValidation.Cross(a, b, c);
        double u = CoverageSupportValidation.Cross(new(point, 0), b, c) / area;
        double v = CoverageSupportValidation.Cross(a, new(point, 0), c) / area;
        double w = 1 - u - v;
        if (u < 0 || v < 0 || w < 0) return false;
        height = (float)(u * a.Z + v * b.Z + w * c.Z);
        return true;
    }

    /// <summary>Exact Euclidean XY distance to the actual boundary, with interpolated boundary height.</summary>
    public void NearestBoundary(Vector2 point, out Vector3 nearest, out float distance)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentException("Query point must be finite.", nameof(point));
        double best = double.PositiveInfinity;
        Vector3 result = default;
        Nearest(0, point, ref best, ref result);
        nearest = result;
        distance = (float)Math.Sqrt(best);
    }

    void Nearest(int index, Vector2 point, ref double best, ref Vector3 result)
    {
        CoverageBvhNode node = EdgeBvh.Nodes[index];
        double dx = Math.Max(Math.Max((double)node.Min.X - point.X, 0), (double)point.X - node.Max.X);
        double dy = Math.Max(Math.Max((double)node.Min.Y - point.Y, 0), (double)point.Y - node.Max.Y);
        if (dx * dx + dy * dy > best) return;
        if (node.Item < 0) { Nearest(node.Left, point, ref best, ref result); Nearest(node.Right, point, ref best, ref result); return; }
        CoverageBoundaryEdge edge = boundary[node.Item];
        double x = (double)edge.B.X - edge.A.X, y = (double)edge.B.Y - edge.A.Y;
        double t = Math.Clamp((((double)point.X - edge.A.X) * x + ((double)point.Y - edge.A.Y) * y) / (x * x + y * y), 0, 1);
        double qx = edge.A.X + t * x, qy = edge.A.Y + t * y;
        double square = (point.X - qx) * (point.X - qx) + (point.Y - qy) * (point.Y - qy);
        if (square < best)
        {
            best = square;
            result = new((float)qx, (float)qy, (float)(edge.A.Z + t * ((double)edge.B.Z - edge.A.Z)));
        }
    }

    static (Vector3 Min, Vector3 Max) Bounds(Vector3 a, Vector3 b, Vector3 c, bool projected)
    {
        Vector3 min = Vector3.Min(a, Vector3.Min(b, c)), max = Vector3.Max(a, Vector3.Max(b, c));
        if (projected) min.Z = max.Z = 0;
        return (min, max);
    }
}
