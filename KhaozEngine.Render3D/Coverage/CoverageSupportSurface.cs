using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KhaozEngine.Render3D;

/// <summary>An approximate compatibility view of a directed boundary edge.</summary>
public readonly record struct CoverageBoundaryEdge(Vector3 A, Vector3 B);

/// <summary>An immutable displayed equipment solid and its certified single-valued inward support.</summary>
public sealed class CoverageSupportSurface
{
    public const int MaxTriangles = 1_024;
    public const int MaxBoundaryEdges = 256;
    readonly CoverageAffinePoint[] projectedPositions;
    readonly CoveragePoint3[] precisePositions;
    readonly Vector3[] positions;
    readonly int[] boundarySourceVertices;
    readonly int[] selected;
    readonly CoverageScalar[] areas;

    CoverageSupportSurface(CoverageSourceTopology source, Matrix4x4 coverageLocal, Matrix4x4 sourceToCoverage,
        CoverageAffinePoint[] projectedPositions, int[] selected, int[] boundarySourceVertices)
    {
        Source = source;
        CoverageLocal = coverageLocal;
        SourceToCoverage = sourceToCoverage;
        this.projectedPositions = projectedPositions;
        precisePositions = projectedPositions.Select(point => point.Approximate).ToArray();
        positions = precisePositions.Select(CoverageGeometry.ToVector3).ToArray();
        this.boundarySourceVertices = boundarySourceVertices;
        this.selected = selected;
        InwardTriangles = Array.AsReadOnly(selected.Select(index => source.Triangles[index]).ToArray());
        Boundary = Array.AsReadOnly(boundarySourceVertices.Select((vertex, i) => new CoverageBoundaryEdge(positions[vertex],
            positions[boundarySourceVertices[(i + 1) % boundarySourceVertices.Length]])).ToArray());
        areas = InwardTriangles.Select(t => CoverageGeometry.Edge(projectedPositions[t.A], projectedPositions[t.B], projectedPositions[t.C])).ToArray();
        SolidBvh = new CoverageBvh(source.Triangles.Select(t => CoverageGeometry.Bounds(projectedPositions[t.A], projectedPositions[t.B], projectedPositions[t.C], false)).ToArray());
        ProjectedBvh = new CoverageBvh(InwardTriangles.Select(t => CoverageGeometry.Bounds(projectedPositions[t.A], projectedPositions[t.B], projectedPositions[t.C], true)).ToArray());
        EdgeBvh = new CoverageBvh(boundarySourceVertices.Select((vertex, i) => CoverageGeometry.Bounds(projectedPositions[vertex],
            projectedPositions[boundarySourceVertices[(i + 1) % boundarySourceVertices.Length]], projectedPositions[vertex], true)).ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes("KhaozEngine.CoverageSupport.v1\0" + source.SourceSha256));
        Add(projectedPositions); Add(boundarySourceVertices); Add(source.Vertices.ToArray()); Add(source.Triangles.ToArray());
        Add(source.Occurrences.ToArray()); Add(source.Materials.ToArray()); Add(selected); Add(new[] { coverageLocal, sourceToCoverage });
        void Add<T>(T[] values) where T : unmanaged
        {
            hash.AppendData(BitConverter.GetBytes(values.Length));
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        Identity = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public CoverageSourceTopology Source { get; }
    public Matrix4x4 CoverageLocal { get; }
    /// <summary>The authoritative float coefficients of the compiled projection. Inversion error is a separate certification obligation.</summary>
    public Matrix4x4 SourceToCoverage { get; }
    public string Identity { get; }
    /// <summary>Approximate compatibility view. Do not use for certification.</summary>
    public ReadOnlySpan<Vector3> Positions => positions;
    /// <summary>Approximate double view. Affine-sum tails remain in ProjectedPositions.</summary>
    public ReadOnlySpan<CoveragePoint3> PrecisePositions => precisePositions;
    public ReadOnlySpan<CoverageAffinePoint> ProjectedPositions => projectedPositions;
    /// <summary>Actual boundary loop, indexing ProjectedPositions and retaining straight rim vertices.</summary>
    public ReadOnlySpan<int> BoundarySourceVertices => boundarySourceVertices;
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
        var positions = new CoverageAffinePoint[source.Positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = CoverageAffinePoint.Project(source.Positions[i], inverse);
            if (!CoverageGeometry.FitsFloat(positions[i].X) || !CoverageGeometry.FitsFloat(positions[i].Y) || !CoverageGeometry.FitsFloat(positions[i].Z))
                throw source.Error("Coverage transform produced an uncertifiable position.");
        }
        int[] boundary = CoverageSupportValidation.Validate(source, positions, selected);
        return new CoverageSupportSurface(source, coverageLocal, inverse, positions, selected, boundary);
    }

    /// <summary>Compatibility query with a rounded result. Certification uses TryGetHeightBounds.</summary>
    public bool TryGetHeight(Vector2 point, out float height)
    {
        bool found = TryGetHeight(point.X, point.Y, out double value);
        height = (float)value;
        return found;
    }
    public bool TryGetHeight(double x, double y, out double height)
    {
        height = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !TryGetHeightBounds(CoverageAffinePoint.FromDouble(x, y), out var bounds)) return false;
        height = bounds.Midpoint;
        return true;
    }

    /// <summary>Exact membership in the affine sheet, with finite outward interpolation bounds. Uncertifiable results refuse.</summary>
    public bool TryGetHeightBounds(CoverageAffinePoint point, out CoverageScalarBounds height) => HeightAt(0, point, out height);

    bool HeightAt(int index, CoverageAffinePoint point, out CoverageScalarBounds height)
    {
        height = default;
        CoverageBvhNode node = ProjectedBvh.Nodes[index];
        if (CoverageGeometry.Compare(point.X, CoverageCoordinate.FromDouble(node.Min.X)) < 0
            || CoverageGeometry.Compare(point.X, CoverageCoordinate.FromDouble(node.Max.X)) > 0
            || CoverageGeometry.Compare(point.Y, CoverageCoordinate.FromDouble(node.Min.Y)) < 0
            || CoverageGeometry.Compare(point.Y, CoverageCoordinate.FromDouble(node.Max.Y)) > 0) return false;
        if (node.Item < 0) return HeightAt(node.Left, point, out height) || HeightAt(node.Right, point, out height);
        CoverageSourceTriangle triangle = InwardTriangles[node.Item];
        CoverageAffinePoint a = projectedPositions[triangle.A], b = projectedPositions[triangle.B], c = projectedPositions[triangle.C];
        CoverageScalar area = areas[node.Item], u = CoverageGeometry.Edge(b, c, point),
            v = CoverageGeometry.Edge(c, a, point), w = CoverageGeometry.Edge(a, b, point);
        if (!u.IsFinite || !v.IsFinite || !w.IsFinite || u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) return false;
        if (CoverageGeometry.SameXY(point, a)) height = a.Z.Bounds;
        else if (CoverageGeometry.SameXY(point, b)) height = b.Z.Bounds;
        else if (CoverageGeometry.SameXY(point, c)) height = c.Z.Bounds;
        else if (w.Sign == 0) height = EdgeHeight(a, b, point);
        else if (u.Sign == 0) height = EdgeHeight(b, c, point);
        else if (v.Sign == 0) height = EdgeHeight(c, a, point);
        else
        {
            var numerator = CoverageIntervals.Add(CoverageIntervals.Multiply(u.Bounds, a.Z.Bounds),
                CoverageIntervals.Add(CoverageIntervals.Multiply(v.Bounds, b.Z.Bounds), CoverageIntervals.Multiply(w.Bounds, c.Z.Bounds)));
            height = CoverageIntervals.Divide(numerator, area.Bounds);
        }
        return height.IsFinite;
    }

    static CoverageScalarBounds EdgeHeight(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint point)
    {
        if (CoverageGeometry.After(a, b)) (a, b) = (b, a);
        CoverageScalar x = CoverageGeometry.Difference(b.X, a.X), y = CoverageGeometry.Difference(b.Y, a.Y);
        bool useX = Math.Abs(x.Estimate) >= Math.Abs(y.Estimate);
        var t = CoverageIntervals.Divide(CoverageGeometry.Difference(useX ? point.X : point.Y, useX ? a.X : a.Y).Bounds,
            useX ? x.Bounds : y.Bounds);
        return CoverageIntervals.Add(CoverageIntervals.Multiply(CoverageIntervals.Subtract(CoverageIntervals.One, t), a.Z.Bounds),
            CoverageIntervals.Multiply(t, b.Z.Bounds));
    }

    /// <summary>Compatibility query. Rounded outputs are not a clearance certificate.</summary>
    public void NearestBoundary(Vector2 point, out Vector3 nearest, out float distance)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentException("Query point must be finite.", nameof(point));
        NearestBoundary(point.X, point.Y, out CoveragePoint3 preciseNearest, out double preciseDistance);
        nearest = CoverageGeometry.ToVector3(preciseNearest);
        distance = (float)preciseDistance;
    }
    public void NearestBoundary(double x, double y, out CoveragePoint3 nearest, out double distance)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !TryGetBoundaryBounds(CoverageAffinePoint.FromDouble(x, y), out var bounds))
            throw new ArgumentException("Query distance is numerically uncertifiable.");
        nearest = bounds.ApproximateNearest;
        distance = bounds.Distance.Midpoint;
    }

    /// <summary>Bounds distance and all numerically possible nearest boundary heights. Uncertifiable results refuse.</summary>
    public bool TryGetBoundaryBounds(CoverageAffinePoint point, out CoverageBoundaryBounds result)
    {
        result = default;
        double lower = double.PositiveInfinity, upper = double.PositiveInfinity;
        CoveragePoint3 approximate = default;
        if (!FindBoundary(0, point, ref lower, ref upper, ref approximate)) return false;
        double heightMin = double.PositiveInfinity, heightMax = double.NegativeInfinity;
        if (!GatherBoundaryHeights(0, point, upper, ref heightMin, ref heightMax)) return false;
        result = new(approximate, new(lower, upper), new(heightMin, heightMax));
        return result.Distance.IsFinite && result.Height.IsFinite;
    }

    bool FindBoundary(int index, CoverageAffinePoint point, ref double lower, ref double upper, ref CoveragePoint3 approximate)
    {
        CoverageBvhNode node = EdgeBvh.Nodes[index];
        if (PruneBoundary(node, point, upper)) return true;
        if (node.Item < 0) return FindBoundary(node.Left, point, ref lower, ref upper, ref approximate)
            && FindBoundary(node.Right, point, ref lower, ref upper, ref approximate);
        if (!BoundaryCandidate(node.Item, point, out var candidate)) return false;
        lower = Math.Min(lower, candidate.Distance.Min);
        if (candidate.Distance.Max < upper) { upper = candidate.Distance.Max; approximate = candidate.ApproximateNearest; }
        return true;
    }

    bool GatherBoundaryHeights(int index, CoverageAffinePoint point, double upper, ref double heightMin, ref double heightMax)
    {
        CoverageBvhNode node = EdgeBvh.Nodes[index];
        if (PruneBoundary(node, point, upper)) return true;
        if (node.Item < 0) return GatherBoundaryHeights(node.Left, point, upper, ref heightMin, ref heightMax)
            && GatherBoundaryHeights(node.Right, point, upper, ref heightMin, ref heightMax);
        if (!BoundaryCandidate(node.Item, point, out var candidate)) return false;
        if (candidate.Distance.Min > upper) return true;
        heightMin = Math.Min(heightMin, candidate.Height.Min); heightMax = Math.Max(heightMax, candidate.Height.Max);
        return true;
    }

    // Every exact edge is inside its outward float box. Only a certified distance lower bound may prune it.
    static bool PruneBoundary(CoverageBvhNode node, CoverageAffinePoint point, double upper)
    {
        if (!double.IsFinite(upper)) return false;
        var dx = BoxGap(point.X, node.Min.X, node.Max.X);
        var dy = BoxGap(point.Y, node.Min.Y, node.Max.Y);
        var distance = CoverageIntervals.Sqrt(CoverageIntervals.Add(CoverageIntervals.Square(dx), CoverageIntervals.Square(dy)));
        return distance.IsFinite && distance.Min > upper;
    }

    static CoverageScalarBounds BoxGap(CoverageCoordinate point, float minimum, float maximum)
    {
        var min = CoverageCoordinate.FromDouble(minimum); var max = CoverageCoordinate.FromDouble(maximum);
        if (CoverageGeometry.Compare(point, min) < 0) return CoverageGeometry.Difference(min, point).Bounds;
        if (CoverageGeometry.Compare(point, max) > 0) return CoverageGeometry.Difference(point, max).Bounds;
        return CoverageIntervals.Zero;
    }

    bool BoundaryCandidate(int index, CoverageAffinePoint point, out CoverageBoundaryBounds result)
    {
        result = default;
        CoverageAffinePoint a = projectedPositions[boundarySourceVertices[index]],
            b = projectedPositions[boundarySourceVertices[(index + 1) % boundarySourceVertices.Length]];
        CoverageScalar dot = CoverageGeometry.Dot(a, point, a, b), length = CoverageGeometry.Dot(a, b, a, b);
        if (!dot.IsFinite || !length.IsFinite || length.Bounds.Min <= 0) return false;
        var t = CoverageIntervals.ClampUnit(CoverageIntervals.Divide(dot.Bounds, length.Bounds));
        var dx = CoverageGeometry.Difference(b.X, a.X); var dy = CoverageGeometry.Difference(b.Y, a.Y);
        var rx = CoverageIntervals.Subtract(CoverageGeometry.Difference(point.X, a.X).Bounds, CoverageIntervals.Multiply(t, dx.Bounds));
        var ry = CoverageIntervals.Subtract(CoverageGeometry.Difference(point.Y, a.Y).Bounds, CoverageIntervals.Multiply(t, dy.Bounds));
        var distance = CoverageIntervals.Sqrt(CoverageIntervals.Add(CoverageIntervals.Square(rx), CoverageIntervals.Square(ry)));
        var dz = CoverageGeometry.Difference(b.Z, a.Z);
        var height = CoverageIntervals.Add(a.Z.Bounds, CoverageIntervals.Multiply(t, dz.Bounds));
        if (!t.IsFinite || !distance.IsFinite || !height.IsFinite) return false;
        double estimate = Math.Clamp(dot.Estimate / length.Estimate, 0, 1);
        result = new(new(a.X.Value + estimate * dx.Estimate, a.Y.Value + estimate * dy.Estimate, a.Z.Value + estimate * dz.Estimate), distance, height);
        return true;
    }
}
