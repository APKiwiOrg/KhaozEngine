using System;
using System.Numerics;
using System.Collections.Generic;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionShapeMeshTests
{
    static readonly CollisionOverlayPalette P = new();

    // Skip near-degenerate triangles (e.g. sphere pole fans collapse to ~zero area).
    const float MinTriangleArea = 1e-6f;

    static (Vector3 Min, Vector3 Max) Bounds(GltfMesh m)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in m.Vertices) { mn = Vector3.Min(mn, v.Position); mx = Vector3.Max(mx, v.Position); }
        return (mn, mx);
    }

    /// <summary>Asserts every non-degenerate triangle in the mesh winds outward relative to
    /// <paramref name="shapeCentroid"/>: Dot(faceNormal, faceCentroid - shapeCentroid) &gt;= 0.
    /// Only valid for star-convex shapes (box, sphere, capsule, cylinder about their own centroid).</summary>
    static void AssertFacesWindOutward(GltfMesh m, Vector3 shapeCentroid)
    {
        var idx = m.Indices32;
        for (int t = 0; t < idx.Length; t += 3)
        {
            Vector3 a = m.Vertices[idx[t]].Position;
            Vector3 b = m.Vertices[idx[t + 1]].Position;
            Vector3 c = m.Vertices[idx[t + 2]].Position;

            Vector3 cross = Vector3.Cross(b - a, c - a);
            float area = cross.Length() * 0.5f;
            if (area < MinTriangleArea) continue; // degenerate (e.g. pole fan), skip.

            Vector3 faceNormal = cross / (area * 2f);
            Vector3 faceCentroid = (a + b + c) / 3f;
            float dot = Vector3.Dot(faceNormal, faceCentroid - shapeCentroid);
            Assert.True(dot >= -1e-4f,
                $"Triangle at index {t} winds inward: dot={dot}, normal={faceNormal}, centroid={faceCentroid}");
        }
    }

    [Fact]
    public void Box_is_centered_with_full_extents()
    {
        var m = CollisionShapeMesh.Build(new BoxShape(new Vector3(1, 2, 3)), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(new Vector3(-1, -2, -3), mn, Comparer);
        Assert.Equal(new Vector3(1, 2, 3), mx, Comparer);
        Assert.True(m.Indices32.Length >= 36);
    }

    [Fact]
    public void Cylinder_is_base_aligned()
    {
        var m = CollisionShapeMesh.Build(new CylinderShape(0.5f, 2f), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(0f, mn.Y, 3);          // base at local Y=0
        Assert.Equal(2f, mx.Y, 3);          // top at length
        Assert.Equal(0.5f, mx.X, 2);        // radius
    }

    [Fact]
    public void Capsule_is_symmetric_with_total_height()
    {
        float r = 0.5f, len = 2f;
        var m = CollisionShapeMesh.Build(new CapsuleShape(r, len), P);
        var (mn, mx) = Bounds(m);
        float total = 2 * r + len;
        Assert.Equal(-total / 2f, mn.Y, 2);
        Assert.Equal(total / 2f, mx.Y, 2);
    }

    [Fact]
    public void Sphere_is_centered_radius()
    {
        var m = CollisionShapeMesh.Build(new SphereShape(1.5f), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(1.5f, mx.X, 1);
        Assert.Equal(-1.5f, mn.X, 1);
    }

    [Fact]
    public void ConvexHull_triangulates_points()
    {
        var pts = new[]
        {
            new Vector3(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
            new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
        };
        var m = CollisionShapeMesh.Build(new ConvexHullShape(pts), P);
        Assert.Equal(12, m.Indices32.Length / 3);
    }

    [Fact]
    public void TriangleMesh_is_passed_through()
    {
        var v = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var i = new[] { 0, 1, 2 };
        var m = CollisionShapeMesh.Build(new TriangleMeshShape(v, i), P);
        Assert.Equal(3, m.Vertices.Length);
        Assert.Equal(new[] { 0u, 1u, 2u }, m.Indices32);
    }

    [Fact]
    public void Kind_color_is_baked_into_vertices()
    {
        var m = CollisionShapeMesh.Build(new BoxShape(Vector3.One), P);
        Vector4 expected = P.For(CollisionShapeKind.Box).ToVector4();
        Assert.All(m.Vertices, v => Assert.Equal(expected, v.Color));
    }

    [Fact]
    public void Compound_composes_child_local_pose_and_colors_per_kind()
    {
        var child = new CompoundChild(new BoxShape(new Vector3(0.5f)), new Pose(new Vector3(5, 0, 0), Quaternion.Identity));
        var m = CollisionShapeMesh.Build(new CompoundShape(new[] { child }), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(4.5f, mn.X, 2);   // 5 - 0.5, child shifted by local pose
        Assert.Equal(5.5f, mx.X, 2);
        Vector4 boxColor = P.For(CollisionShapeKind.Box).ToVector4();
        Assert.All(m.Vertices, v => Assert.Equal(boxColor, v.Color));
    }

    [Fact]
    public void Box_faces_wind_outward()
    {
        var m = CollisionShapeMesh.Build(new BoxShape(new Vector3(1, 2, 3)), P);
        AssertFacesWindOutward(m, Vector3.Zero);
    }

    [Fact]
    public void Cylinder_faces_wind_outward()
    {
        float length = 2f;
        var m = CollisionShapeMesh.Build(new CylinderShape(0.5f, length), P);
        // Base-aligned: geometric center is (0, length/2, 0).
        AssertFacesWindOutward(m, new Vector3(0, length / 2f, 0));
    }

    [Fact]
    public void Sphere_faces_wind_outward()
    {
        var m = CollisionShapeMesh.Build(new SphereShape(1.5f), P);
        AssertFacesWindOutward(m, Vector3.Zero);
    }

    [Fact]
    public void Capsule_has_cylindrical_midsection()
    {
        float radius = 0.5f, length = 2f;
        var m = CollisionShapeMesh.Build(new CapsuleShape(radius, length), P);
        float halfLen = length * 0.5f;
        const float tol = 1e-3f;

        bool HasFullRadiusRingAt(float y) =>
            Array.Exists(m.Vertices, v =>
                MathF.Abs(v.Position.Y - y) < tol &&
                MathF.Abs(MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z) - radius) < tol);

        Assert.True(HasFullRadiusRingAt(-halfLen), "Expected a full-radius ring at the bottom equator (y = -Length/2).");
        Assert.True(HasFullRadiusRingAt(halfLen), "Expected a full-radius ring at the top equator (y = +Length/2).");

        AssertFacesWindOutward(m, Vector3.Zero);
    }

    static readonly VecComparer Comparer = new();
    sealed class VecComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
