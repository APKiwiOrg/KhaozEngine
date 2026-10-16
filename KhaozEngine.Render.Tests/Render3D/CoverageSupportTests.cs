using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSupportTests
{
    [Fact]
    public void Compiled_sheet_retains_solid_provenance_and_exact_height_queries()
    {
        Vector3[] positions = { new(0, 0, 2), new(2, 0, 4), new(2, 2, 6), new(0, 2, 4), new(0, 0, 9) };
        var source = Source(positions, new[] { (0, 1, 2), (0, 2, 3), (0, 4, 1) });
        var support = CoverageSupportSurface.Compile(source, new[] { new CoverageTriangleRange(0, 0, 0, 2) }, Matrix4x4.Identity);
        positions[0] = new Vector3(99);
        Assert.Same(source, support.Source);
        Assert.Equal(3, support.Source.Triangles.Count);
        Assert.Equal(2, support.InwardTriangles.Count);
        Assert.Equal(4, support.Boundary.Count);
        Assert.True(support.TryGetHeight(new Vector2(.5f, 1), out float height));
        Assert.Equal(3.5f, height);
        Assert.False(support.TryGetHeight(new Vector2(3, 1), out _));
        support.NearestBoundary(new Vector2(3, 1), out Vector3 nearest, out float distance);
        Assert.Equal(new Vector3(2, 1, 5), nearest);
        Assert.Equal(1, distance);
        Assert.Equal(support.Identity, CoverageSupportSurface.Compile(source,
            new[] { new CoverageTriangleRange(0, 0, 0, 2) }, Matrix4x4.Identity).Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_edge_height_membership_is_watertight(bool reverse)
    {
        Vector3[] points = { new(0, -.3296944200992584f, -.3296944200992584f),
            new(0, .31033483147621155f, .31033483147621155f),
            new(.6288225650787354f, .7649580240249634f, .7649580240249634f),
            new(-.23842231929302216f, .010567640885710716f, .010567640885710716f) };
        var triangles = reverse ? new[] { (0, 2, 1), (1, 3, 0) } : new[] { (0, 1, 2), (1, 0, 3) };
        var support = CoverageSupportSurface.Compile(Source(points, triangles),
            new[] { new CoverageTriangleRange(0, 0, 0, 2) }, Matrix4x4.Identity);
        var point = new Vector2(0, .04728425294160843f);
        Assert.True(support.TryGetHeight(point, out float height));
        Assert.Equal(point.Y, height);
        foreach (Vector3 vertex in points)
        {
            Assert.True(support.TryGetHeight(new(vertex.X, vertex.Y), out height));
            Assert.Equal(vertex.Z, height);
        }
        Assert.False(support.TryGetHeight(new(0, MathF.BitDecrement(points[0].Y)), out _));
    }

    [Fact]
    public void Height_membership_excludes_a_negative_edge_inside_the_bounds()
    {
        var support = CoverageSupportSurface.Compile(Source(new Vector3[] { new(0, 0, 0), new(1, 0, 1), new(0, 1, 2) },
            new[] { (0, 1, 2) }), new[] { new CoverageTriangleRange(0, 0, 0, 1) }, Matrix4x4.Identity);
        Assert.False(support.TryGetHeight(new(.5f, MathF.BitIncrement(.5f)), out _));
        Assert.False(support.TryGetHeight(.5, Math.BitIncrement(.5), out _));
    }

    [Fact]
    public void Rotated_straight_subdivided_rim_keeps_its_actual_sheet()
    {
        var (points, triangles) = SubdividedRim();
        var frame = Matrix4x4.CreateRotationZ(.5f);
        var support = CoverageSupportSurface.Compile(Source(points, triangles),
            new[] { new CoverageTriangleRange(0, 0, 0, triangles.Length) }, frame);
        Assert.Equal(11, support.Boundary.Count);
        Assert.Equal(11, support.BoundarySourceVertices.Length);
        Assert.Equal(11, support.BoundarySourceVertices.ToArray().Distinct().Count());
        Assert.Equal(9, support.InwardTriangles.Count);
        Assert.Equal(points, support.Source.Positions.ToArray());
        Assert.True(Matrix4x4.Invert(frame, out Matrix4x4 inverse));
        Vector2 point = Vector2.Transform(new(.5f, .5f), inverse);
        Assert.True(support.TryGetHeight(point, out float height));
        Assert.Equal(3f, height, 6);
        double x = .5 * inverse.M11 + .5 * inverse.M21 + inverse.M41;
        double y = .5 * inverse.M12 + .5 * inverse.M22 + inverse.M42;
        Assert.True(support.TryGetHeight(x, y, out double preciseHeight));
        Assert.Equal(3, preciseHeight, 12);
        foreach (CoverageBvhNode node in support.ProjectedBvh.Nodes)
        {
            if (node.Item < 0) continue;
            CoverageSourceTriangle triangle = support.InwardTriangles[node.Item];
            foreach (int corner in new[] { triangle.A, triangle.B, triangle.C })
            {
                CoveragePoint3 vertex = support.PrecisePositions[corner];
                Assert.True(vertex.X >= node.Min.X && vertex.X <= node.Max.X);
                Assert.True(vertex.Y >= node.Min.Y && vertex.Y <= node.Max.Y);
                Assert.True(support.TryGetHeight(vertex.X, vertex.Y, out double vertexHeight));
                Assert.Equal(vertex.Z, vertexHeight);
            }
        }
        double outsideX = .5 * inverse.M11 - .125 * inverse.M21 + inverse.M41;
        double outsideY = .5 * inverse.M12 - .125 * inverse.M22 + inverse.M42;
        support.NearestBoundary(outsideX, outsideY, out CoveragePoint3 nearest, out double distance);
        Assert.Equal(support.PrecisePositions[4].X, nearest.X, 12);
        Assert.Equal(support.PrecisePositions[4].Y, nearest.Y, 12);
        Assert.Equal(2.5, nearest.Z, 12);
        Assert.Equal(.125 * Math.Sqrt((double)inverse.M21 * inverse.M21 + (double)inverse.M22 * inverse.M22), distance, 12);
        foreach (CoverageBvhNode node in support.EdgeBvh.Nodes)
        {
            if (node.Item < 0) continue;
            foreach (int vertexIndex in new[] { node.Item, (node.Item + 1) % support.BoundarySourceVertices.Length })
            {
                CoveragePoint3 endpoint = support.PrecisePositions[support.BoundarySourceVertices[vertexIndex]];
                Assert.True(endpoint.X >= node.Min.X && endpoint.X <= node.Max.X);
                Assert.True(endpoint.Y >= node.Min.Y && endpoint.Y <= node.Max.Y);
            }
        }
        CoveragePoint3 rim = support.PrecisePositions[1];
        Assert.Equal((double).125f * inverse.M11, rim.X);
        Assert.Equal((double).125f * inverse.M12, rim.Y);
        Assert.Equal(0, support.Source.Positions[1].Y);
    }

    [Fact]
    public void Translated_multi_term_affine_rim_stays_straight()
    {
        var (points, triangles) = SubdividedRim();
        for (int i = 0; i < points.Length; i++) points[i].Y += points[i].X;
        // Its inverse projects X as 2^30 + source.X + source.Y * 2^-22.
        // At the middle rim point, rounding that sum to double crosses a half-ulp tie.
        var frame = new Matrix4x4(1, 0, 0, 0, -1f / 4_194_304f, 1, 0, 0,
            0, 0, 1, 0, -1_073_741_568f, -1_073_741_824f, 0, 1);
        var support = CoverageSupportSurface.Compile(Source(points, triangles),
            new[] { new CoverageTriangleRange(0, 0, 0, triangles.Length) }, frame);
        Assert.Equal(11, support.BoundarySourceVertices.Length);
        Assert.Equal(points, support.Source.Positions.ToArray());
        Assert.Equal(1f / 4_194_304f, support.SourceToCoverage.M21);
        Assert.Equal(1_073_741_824f, support.SourceToCoverage.M41);
        for (int i = 0; i < 9; i++)
        {
            var point = CoverageAffinePoint.Project(points[i], support.SourceToCoverage);
            Assert.True(support.TryGetHeightBounds(point, out var vertexHeight));
            Assert.True(vertexHeight.Min <= points[i].Z && vertexHeight.Max >= points[i].Z);
            Assert.True(vertexHeight.Max - vertexHeight.Min < 1e-12);
        }
        var middle = CoverageAffinePoint.Project(new(.5f, 1, 3), support.SourceToCoverage);
        Assert.True(support.TryGetHeightBounds(middle, out var height));
        Assert.True(height.Min <= 3 && height.Max >= 3);
        Assert.True(height.Max - height.Min < 1e-12);
        Assert.NotEqual(0, support.ProjectedPositions[5].X[1]);
        var outside = CoverageAffinePoint.Project(new(-.125f, -.125f, 0), support.SourceToCoverage);
        Assert.True(support.TryGetBoundaryBounds(outside, out var boundary));
        Assert.True(boundary.Height.Min <= 2 && boundary.Height.Max >= 2);
        Assert.True(boundary.Height.Max - boundary.Height.Min < 1e-12);
        double slope = 1 + (double)support.SourceToCoverage.M21;
        double expectedDistance = .125 * Math.Sqrt(slope * slope + 1);
        Assert.True(boundary.Distance.Min <= expectedDistance && boundary.Distance.Max >= expectedDistance);
        Assert.True(boundary.Distance.Max - boundary.Distance.Min < 1e-12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var projectedMiddle = CoverageAffinePoint.Project(new(.5f, 1, 3), support.SourceToCoverage);
        bool heightFound = support.TryGetHeightBounds(projectedMiddle, out var measuredHeight);
        bool boundaryFound = support.TryGetBoundaryBounds(outside, out var measuredBoundary);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(heightFound && boundaryFound);
        Assert.True(measuredHeight.IsFinite && measuredBoundary.Distance.IsFinite && measuredBoundary.Height.IsFinite);
        Assert.Equal(0, allocated);
        Assert.False(support.TryGetBoundaryBounds(CoverageAffinePoint.FromDouble(double.MaxValue, double.MaxValue), out _));
        foreach (CoverageBvhNode node in support.ProjectedBvh.Nodes)
        {
            if (node.Item < 0) continue;
            var triangle = support.InwardTriangles[node.Item];
            foreach (int corner in new[] { triangle.A, triangle.B, triangle.C })
            {
                var vertex = support.ProjectedPositions[corner];
                Assert.True(node.Min.X <= vertex.X.Bounds.Min && node.Max.X >= vertex.X.Bounds.Max);
                Assert.True(node.Min.Y <= vertex.Y.Bounds.Min && node.Max.Y >= vertex.Y.Bounds.Max);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotated_rim_with_a_real_small_concavity_refuses(bool translated)
    {
        var (points, triangles) = SubdividedRim();
        if (translated)
            for (int i = 0; i < points.Length; i++) points[i].Y += points[i].X;
        points[4].Y = translated ? MathF.BitIncrement(points[4].Y) : 1f / 268_435_456f;
        var frame = translated ? new Matrix4x4(1, 0, 0, 0, -1f / 4_194_304f, 1, 0, 0,
            0, 0, 1, 0, -1_073_741_568f, -1_073_741_824f, 0, 1) : Matrix4x4.CreateRotationZ(.5f);
        var error = Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(Source(points, triangles),
            new[] { new CoverageTriangleRange(0, 0, 0, triangles.Length) }, frame));
        Assert.Contains("Support boundary is not convex", error.Message);
    }

    static (Vector3[] Points, (int A, int B, int C)[] Triangles) SubdividedRim()
    {
        var points = Enumerable.Range(0, 9).Select(i => new Vector3(i / 8f, 0, 2 + i / 8f))
            .Concat(new Vector3[] { new(1, 1, 4), new(0, 1, 3) }).ToArray();
        var triangles = Enumerable.Range(0, 8).Select(i => (10, i, i + 1)).Append((10, 8, 9)).ToArray();
        return (points, triangles);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("range")]
    [InlineData("duplicate")]
    [InlineData("mirror")]
    [InlineData("singular")]
    [InlineData("projective")]
    [InlineData("two-depths")]
    [InlineData("overlap")]
    [InlineData("hole")]
    [InlineData("concave")]
    [InlineData("seam-height")]
    public void Unsupported_sheet_refuses(string kind)
    {
        Vector3[] positions = { new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0) };
        (int, int, int)[] triangles = { (0, 1, 2), (0, 2, 3) };
        var frame = Matrix4x4.Identity;
        if (kind == "mirror") frame = Matrix4x4.CreateScale(-1, 1, 1);
        if (kind == "singular") frame = Matrix4x4.CreateScale(0);
        if (kind == "projective") frame.M14 = .1f;
        if (kind == "two-depths" || kind == "seam-height")
        {
            positions = positions.Concat(positions.Select(v => v + Vector3.UnitZ)).ToArray();
            triangles = kind == "two-depths" ? new[] { (0, 1, 2), (4, 5, 6) } : new[] { (0, 1, 2), (4, 6, 7) };
        }
        if (kind == "overlap") triangles = new[] { (0, 1, 2), (0, 1, 3) };
        if (kind == "concave") positions[2] = new(.5f, .5f, 0);
        if (kind == "hole")
        {
            positions = new Vector3[] { new(0, 0, 0), new(3, 0, 0), new(3, 3, 0), new(0, 3, 0), new(1, 1, 0), new(2, 1, 0), new(2, 2, 0), new(1, 2, 0) };
            triangles = new[] { (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5), (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7) };
        }
        var ranges = kind == "empty" ? Array.Empty<CoverageTriangleRange>() : kind == "duplicate"
            ? new[] { new CoverageTriangleRange(0, 0, 0, 2), new CoverageTriangleRange(0, 0, 0, 1) }
            : new[] { new CoverageTriangleRange(0, 0, 0, kind == "range" ? 3 : triangles.Length) };
        var source = Source(positions, triangles);
        var error = Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source, ranges, frame));
        Assert.Contains("synthetic", error.Message);
        string? reason = kind switch
        {
            "concave" => "Support boundary is not convex",
            "hole" => "Support sheet has a hole or disconnected occurrence",
            "overlap" => "Support topology is nonmanifold",
            "two-depths" or "seam-height" => "Support has inconsistent shared heights or multiple depths",
            _ => null,
        };
        if (reason != null) Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Coverage_local_uses_full_affine_inverse()
    {
        var source = Source(new Vector3[] { new(5, 7, 11), new(7, 7, 11), new(5, 10, 11) }, new[] { (0, 1, 2) });
        var frame = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(5, 7, 11);
        var support = CoverageSupportSurface.Compile(source, new[] { new CoverageTriangleRange(0, 0, 0, 1) }, frame);
        Assert.True(support.TryGetHeight(new Vector2(.25f, .25f), out float height));
        Assert.Equal(0, height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Support_capacity_refuses_before_building_queries(bool boundaryLimit)
    {
        int count = boundaryLimit ? 257 : 1025;
        Vector3[] positions = new Vector3[count + 1];
        for (int i = 0; i < count; i++)
        {
            double angle = i * 2 * Math.PI / count;
            positions[i + 1] = new((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
        }
        var triangles = Enumerable.Range(0, count).Select(i => (0, i + 1, (i + 1) % count + 1)).ToArray();
        var source = Source(positions, triangles);
        Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source,
            new[] { new CoverageTriangleRange(0, 0, 0, count) }, Matrix4x4.Identity));
    }

    internal static CoverageSourceTopology Source(Vector3[] positions, (int A, int B, int C)[] triangles) => new("synthetic", "fixture",
        positions, Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)).ToArray(),
        triangles.Select((t, i) => new CoverageSourceTriangle(0, 0, i, -1, 0, t.A, t.B, t.C)).ToArray());
}
