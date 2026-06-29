using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionBakeTests
{
    [Fact]
    public void SolidProp_BakesAConvexHull_RoundTrips()
    {
        // A rock-like short solid prop bakes to a CONVEX HULL (v4 fix), not a triangle mesh. A convex shape can
        // never trap the capsule (a unique shortest exit always points out), where a one-sided non-convex mesh
        // sucked the capsule through the near face and pinned it. The hull is the TRUE minimal hull of the full
        // deduplicated vertex set (no stride-downsample), so it round-trips through KECL kind 1.
        GltfMesh rock = TestMeshes.UnitIcosphere();
        Assert.False(PropCollisionBake.IsTree(rock), "rock fixture must classify as a solid prop, not a tree");
        Assert.False(PropCollisionBake.IsBuilding(rock), "short rock fixture must not classify as a building");
        PhysicsShape shape = PropCollisionBake.Bake(rock);
        var original = Assert.IsType<ConvexHullShape>(shape);

        // The full deduplicated vertex set is passed to the hull (no stride-downsample): the octahedron's 6 unique
        // verts all survive dedup, so the hull has a reasonable point count (every vertex defines the hull here).
        Assert.True(original.Points.Length >= 4, $"hull should keep its extreme points, got {original.Points.Length}");
        Assert.Equal(6, original.Points.Length);

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var hull = Assert.IsType<ConvexHullShape>(loaded);

        // Lossless round-trip through KECL kind 1: same point count, sampled point matches exactly.
        Assert.Equal(original.Points.Length, hull.Points.Length);
        Vector3 origP0 = original.Points[0];
        Vector3 loadP0 = hull.Points[0];
        Assert.True(MathF.Abs(origP0.X - loadP0.X) < 1e-5f, $"Points[0].X mismatch: {origP0.X} vs {loadP0.X}");
        Assert.True(MathF.Abs(origP0.Y - loadP0.Y) < 1e-5f, $"Points[0].Y mismatch: {origP0.Y} vs {loadP0.Y}");
        Assert.True(MathF.Abs(origP0.Z - loadP0.Z) < 1e-5f, $"Points[0].Z mismatch: {origP0.Z} vs {loadP0.Z}");
    }

    [Fact]
    public void Building_BakesATriangleMesh()
    {
        GltfMesh house = TestMeshes.BoxRoomWithDoorway();
        PhysicsShape shape = PropCollisionBake.Bake(house);
        Assert.IsType<TriangleMeshShape>(shape);
    }

    [Fact]
    public void TriangleMesh_RoundTrips()
    {
        GltfMesh house = TestMeshes.BoxRoomWithDoorway();
        PhysicsShape shape = PropCollisionBake.Bake(house);
        var original = Assert.IsType<TriangleMeshShape>(shape);

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var mesh = Assert.IsType<TriangleMeshShape>(loaded);

        // Lossless round-trip: same vertex and index counts, sampled content matches.
        Assert.Equal(original.Vertices.Length, mesh.Vertices.Length);
        Assert.Equal(original.Indices.Length, mesh.Indices.Length);
        Assert.True(mesh.Vertices.Length >= 3);
        Assert.True(mesh.Indices.Length >= 3);
        Assert.Equal(0, mesh.Indices.Length % 3);

        Vector3 origV0 = original.Vertices[0];
        Vector3 loadV0 = mesh.Vertices[0];
        Assert.True(MathF.Abs(origV0.X - loadV0.X) < 1e-5f, $"Vertices[0].X mismatch: {origV0.X} vs {loadV0.X}");
        Assert.True(MathF.Abs(origV0.Y - loadV0.Y) < 1e-5f, $"Vertices[0].Y mismatch: {origV0.Y} vs {loadV0.Y}");
        Assert.True(MathF.Abs(origV0.Z - loadV0.Z) < 1e-5f, $"Vertices[0].Z mismatch: {origV0.Z} vs {loadV0.Z}");
        Assert.Equal(original.Indices[0], mesh.Indices[0]);
    }
}

/// <summary>Hand-authored mesh fixtures for offline bake tests (no GPU, no glTF).</summary>
static class TestMeshes
{
    /// <summary>A rough convex blob: a regular octahedron (6 verts, 8 triangles). Short (height 2) so
    /// <see cref="PropSurfaceBake.IsWalkableSolid"/> classifies it as a solid prop, not a building.</summary>
    public static GltfMesh UnitIcosphere()
    {
        // Regular octahedron: 6 vertices, 8 triangles.
        var verts = new ModelVertex[]
        {
            V( 0, 1, 0),   // top
            V( 1, 0, 0),   // +X
            V( 0, 0, 1),   // +Z
            V(-1, 0, 0),   // -X
            V( 0, 0,-1),   // -Z
            V( 0,-1, 0),   // bottom
        };
        var idx = new uint[]
        {
            0,1,2,  0,2,3,  0,3,4,  0,4,1,  // top half
            5,2,1,  5,3,2,  5,4,3,  5,1,4,  // bottom half
        };
        return new GltfMesh(verts, idx);
    }

    /// <summary>A hollow box room (4 walls + floor, no ceiling) with a missing wall segment simulating a
    /// doorway. Tall and complex (over the triangle threshold AND over SolidHeightMeters), so it classifies as a
    /// building and bakes to a triangle mesh (concave interior) rather than a convex hull.</summary>
    public static GltfMesh BoxRoomWithDoorway()
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();

        // Build a 6 m tall hollow box room with repeated faces so we exceed the 60-triangle threshold.
        // Floor: one quad.
        AddQuad(verts, idx,
            new Vector3(-5, 0,-5), new Vector3( 5, 0,-5),
            new Vector3( 5, 0, 5), new Vector3(-5, 0, 5));

        // Four walls - each one is a series of repeated quads so we get enough triangles.
        // Repeat 12 times per wall (12*4 walls * 2 tris/quad = 96 triangles + 2 floor = 98 > 60).
        for (int rep = 0; rep < 12; rep++)
        {
            // -Z wall
            AddQuad(verts, idx,
                new Vector3(-5, 0,-5), new Vector3( 5, 0,-5),
                new Vector3( 5, 6,-5), new Vector3(-5, 6,-5));
            // +Z wall
            AddQuad(verts, idx,
                new Vector3( 5, 0, 5), new Vector3(-5, 0, 5),
                new Vector3(-5, 6, 5), new Vector3( 5, 6, 5));
            // -X wall
            AddQuad(verts, idx,
                new Vector3(-5, 0, 5), new Vector3(-5, 0,-5),
                new Vector3(-5, 6,-5), new Vector3(-5, 6, 5));
            // +X wall
            AddQuad(verts, idx,
                new Vector3( 5, 0,-5), new Vector3( 5, 0, 5),
                new Vector3( 5, 6, 5), new Vector3( 5, 6,-5));
        }

        return new GltfMesh(verts.ToArray(), idx.ToArray());
    }

    static void AddQuad(List<ModelVertex> verts, List<uint> idx,
                        Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        uint base0 = (uint)verts.Count;
        verts.Add(V(a)); verts.Add(V(b)); verts.Add(V(c)); verts.Add(V(d));
        idx.Add(base0); idx.Add(base0 + 1); idx.Add(base0 + 2);
        idx.Add(base0); idx.Add(base0 + 2); idx.Add(base0 + 3);
    }

    static ModelVertex V(float x, float y, float z) => new ModelVertex(new Vector3(x, y, z), Vector3.UnitY, Vector4.One);
    static ModelVertex V(Vector3 p)                  => new ModelVertex(p, Vector3.UnitY, Vector4.One);
}
