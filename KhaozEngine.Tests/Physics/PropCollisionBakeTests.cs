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
    public void SolidProp_BakesATriangleMesh_RoundTrips()
    {
        // A rock-like short solid prop now bakes to a TRIANGLE MESH of the exact mesh (FIX 1), not a
        // concavity-filling convex hull: the collider matches the visible surface so the capsule cannot clip it.
        GltfMesh rock = TestMeshes.UnitIcosphere();
        Assert.False(PropCollisionBake.IsTree(rock), "rock fixture must classify as a solid prop, not a tree");
        PhysicsShape shape = PropCollisionBake.Bake(rock);
        var original = Assert.IsType<TriangleMeshShape>(shape);

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var mesh = Assert.IsType<TriangleMeshShape>(loaded);

        // Lossless round-trip: same vertex/index counts and a sampled vertex + index match exactly. The index
        // order is preserved (Bepu meshes are one-sided; outward winding must survive the bake + IO).
        Assert.Equal(original.Vertices.Length, mesh.Vertices.Length);
        Assert.Equal(original.Indices.Length, mesh.Indices.Length);
        Assert.True(mesh.Vertices.Length >= 3);
        Assert.Equal(0, mesh.Indices.Length % 3);
        Vector3 origV0 = original.Vertices[0];
        Vector3 loadV0 = mesh.Vertices[0];
        Assert.True(MathF.Abs(origV0.X - loadV0.X) < 1e-5f, $"Vertices[0].X mismatch: {origV0.X} vs {loadV0.X}");
        Assert.True(MathF.Abs(origV0.Y - loadV0.Y) < 1e-5f, $"Vertices[0].Y mismatch: {origV0.Y} vs {loadV0.Y}");
        Assert.True(MathF.Abs(origV0.Z - loadV0.Z) < 1e-5f, $"Vertices[0].Z mismatch: {origV0.Z} vs {loadV0.Z}");
        for (int i = 0; i < original.Indices.Length; i++)
            Assert.Equal(original.Indices[i], mesh.Indices[i]);
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
    /// doorway. Tall and complex (a building); like every non-tree solid prop it bakes to a triangle mesh.</summary>
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
