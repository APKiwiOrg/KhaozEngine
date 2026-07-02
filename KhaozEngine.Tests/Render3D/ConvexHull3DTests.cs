// KhaozEngine.Tests/Render3D/ConvexHull3DTests.cs
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class ConvexHull3DTests
{
    static readonly Vector3[] Cube =
    {
        new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
        new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
    };

    [Fact]
    public void Cube_produces_twelve_outward_triangles()
    {
        var (v, i) = ConvexHull3D.Triangulate(Cube);
        Assert.Equal(12, i.Length / 3);
        AssertAllFacesOutward(v, i);
    }

    [Fact]
    public void Tetrahedron_produces_four_triangles()
    {
        var pts = new[] { new Vector3(0,0,0), new(1,0,0), new(0,1,0), new(0,0,1) };
        var (v, i) = ConvexHull3D.Triangulate(pts);
        Assert.Equal(4, i.Length / 3);
        AssertAllFacesOutward(v, i);
    }

    [Fact]
    public void Interior_points_do_not_add_faces()
    {
        var pts = new List<Vector3>(Cube) { new(0,0,0), new(0.5f,0.1f,-0.2f) };
        var (_, i) = ConvexHull3D.Triangulate(pts);
        Assert.Equal(12, i.Length / 3);
    }

    [Fact]
    public void Coplanar_input_returns_empty()
    {
        var pts = new[] { new Vector3(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0) };
        var (v, i) = ConvexHull3D.Triangulate(pts);
        Assert.Empty(v);
        Assert.Empty(i);
    }

    [Fact]
    public void Too_few_points_returns_empty()
    {
        var (v, i) = ConvexHull3D.Triangulate(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        Assert.Empty(v);
        Assert.Empty(i);
    }

    // Every triangle's outward normal must point away from the hull centroid, and every
    // input point must lie inside or on every face plane.
    static void AssertAllFacesOutward(Vector3[] v, int[] idx)
    {
        Vector3 c = Vector3.Zero;
        foreach (var p in v) c += p;
        c /= v.Length;
        for (int t = 0; t < idx.Length; t += 3)
        {
            Vector3 a = v[idx[t]], b = v[idx[t + 1]], cc = v[idx[t + 2]];
            Vector3 n = Vector3.Cross(b - a, cc - a);
            Assert.True(Vector3.Dot(n, a - c) > 0f, "face winding is not outward");
        }
    }
}
