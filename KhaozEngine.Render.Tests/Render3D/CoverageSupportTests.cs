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
        Assert.Contains("synthetic", Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source, ranges, frame)).Message);
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
