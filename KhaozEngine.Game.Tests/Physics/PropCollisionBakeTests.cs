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
    public void TrunkHull_TracksLean_AndExcludesCanopy()
    {
        GltfMesh tree = TestMeshes.LeaningTree();
        Assert.True(PropCollisionBake.IsTree(tree), "leaning-tree fixture must classify as a tree");
        PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
        var hull = Assert.IsType<ConvexHullShape>(shape);

        // Canopy (y >= 3.5) is excluded: no hull point above the trunk-band cap (~2.6 m with a 0.1 m margin).
        foreach (Vector3 p in hull.Points)
            Assert.True(p.Y <= 2.6f, $"hull point above the trunk cap (canopy not excluded): y={p.Y}");

        // The trunk leans toward +X with height: the highest hull point sits clearly off the vertical axis,
        // i.e. the hull follows the leaning trunk rather than a base-pinned vertical cylinder.
        Vector3 highest = hull.Points[0];
        foreach (Vector3 p in hull.Points) if (p.Y > highest.Y) highest = p;
        Assert.True(highest.X > 0.3f, $"hull does not track the lean; highest point X={highest.X}");
    }

    [Fact]
    public void TrunkHull_ExcludesWideLowBranches()
    {
        GltfMesh tree = TestMeshes.StraightTrunkWithLowBranches();
        PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
        var hull = Assert.IsType<ConvexHullShape>(shape);

        // Low branches spread to |X|~1.5 at y~1; the radial-core filter drops them, so the hull stays near the
        // ~0.2 m trunk core (well under 0.6 m) for any point at branch height.
        foreach (Vector3 p in hull.Points)
            if (p.Y is > 0.8f and < 1.2f)
                Assert.True(MathF.Abs(p.X) < 0.6f && MathF.Abs(p.Z) < 0.6f,
                    $"wide low branch survived the core filter: {p}");
    }

    [Fact]
    public void TrunkHull_IsSolid_PushesACapsuleOut()
    {
        GltfMesh tree = TestMeshes.LeaningTree();
        PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
        using KhaozEngine.Physics.IPhysicsWorld world = new KhaozEngine.Physics.Bepu.BepuPhysicsWorld();
        world.AddStatic(shape, KhaozEngine.Physics.Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // A capsule overlapping the trunk near the base is pushed out (non-trapping solid).
        var capsule = new KhaozEngine.Physics.CapsuleShape(0.4f, 1.0f);
        bool overlap = world.ComputePenetration(capsule, KhaozEngine.Physics.Pose.At(new Vector3(0.1f, 0.6f, 0f)), out Vector3 mtv);
        Assert.True(overlap, "capsule overlapping the trunk hull should report penetration");
        Assert.True(mtv.Length() > 0f, "push-out MTV should be non-zero");
    }

    [Fact]
    public void TrunkHull_DegenerateTrunk_FallsBackToCylinder()
    {
        GltfMesh tree = TestMeshes.CollinearTrunkTree();
        PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
        Assert.IsType<CylinderShape>(shape);
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

    /// <summary>A tall tree whose trunk centreline LEANS toward +X with height (centre X = 0.2*y), a thin
    /// trunk core (ring radius ~0.18) from y=0..3, and a wide canopy (|x|,|z| up to 2) from y=3.5..5. Tall
    /// (height 5 > 2.5) with a canopy spread > 1.6x the base, so IsWalkableSolid is false (a tree).</summary>
    public static GltfMesh LeaningTree()
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();
        // Trunk rings: centre leans +X with height; small radius so it is clearly a thin trunk.
        for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f)
        {
            float cx = 0.2f * y;            // lean
            const float r = 0.18f;
            AddRing(verts, idx, new Vector3(cx, y, 0f), r);
        }
        // Canopy: wide spread well above the trunk band.
        for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f)
            AddRing(verts, idx, new Vector3(0.6f, y, 0f), 2.0f);
        return new GltfMesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>A straight (un-leaning) tall tree with a thin trunk plus a few WIDE low branch verts at y~1
    /// (|x| up to 1.5), and a wide canopy above. Used to prove the radial-core filter drops the branches.</summary>
    public static GltfMesh StraightTrunkWithLowBranches()
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();
        for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 0.18f);
        // Wide low branches at y~1.
        foreach (float ang in new[] { 0f, 1.57f, 3.14f, 4.71f })
            AddRing(verts, idx, new Vector3(1.5f * MathF.Cos(ang), 1f, 1.5f * MathF.Sin(ang)), 0.1f);
        for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 2.0f);
        return new GltfMesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>A degenerate tree: the trunk band is a single COLLINEAR column of verts on the Y axis (no
    /// volume), with a wide canopy above so it still classifies as a tree. The trunk hull is degenerate so
    /// BakeTrunkHull must fall back to a cylinder.</summary>
    public static GltfMesh CollinearTrunkTree()
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();
        for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f) { uint b = (uint)verts.Count; verts.Add(V(0, y, 0)); idx.Add(b); idx.Add(b); idx.Add(b); }
        for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 2.0f);
        return new GltfMesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>Add an 8-vertex ring (octagon) of radius r centred at c, as 8 degenerate triangles (verts only;
    /// the bake reads positions, not winding).</summary>
    static void AddRing(List<ModelVertex> verts, List<uint> idx, Vector3 c, float r)
    {
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            uint b = (uint)verts.Count;
            verts.Add(V(c.X + r * MathF.Cos(a), c.Y, c.Z + r * MathF.Sin(a)));
            idx.Add(b); idx.Add(b); idx.Add(b);
        }
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
