using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Covers the physics-feel refinement: the general CollisionBatcher depenetration path
/// (replaces the box/sphere-only analytic switch that left the capsule trapped in hulls/meshes), the
/// tree trunk-cylinder bake + base-aligned placement, the platform box, and the per-iteration slide
/// depenetration. Test 1 (HullPenetration_PushesOut) LOCKS the contact-normal sign.</summary>
public class PhysicsFeelTests
{
    // A 1x1x1 unit cube convex hull centred at origin (ConvexHullHelper recenters to centroid = origin).
    static ConvexHullShape UnitCubeHull()
    {
        var points = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f),
        };
        return new ConvexHullShape(points);
    }

    // ---- Test 1: hull penetration MTV pushes OUT (LOCKS THE SIGN) ----
    [Fact]
    public void HullPenetration_PushesOut_PositiveX()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(UnitCubeHull(), Pose.At(Vector3.Zero)); // half-extent 0.5 each axis
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // Capsule centre at +X just outside the box centre, overlapping the +X flank.
        // Box +X face at x=0.5; capsule (radius 0.4) centre at x=0.6 reaches in to x=0.2 -> overlap 0.3.
        var capPose = Pose.At(new Vector3(0.6f, 0f, 0f));
        bool overlap = world.ComputePenetration(cap, capPose, out Vector3 mtv);

        Assert.True(overlap, "capsule overlapping the hull flank must report penetration");
        // The MTV must push the capsule OUT, away from the static, along +X (NOT negated).
        Assert.True(mtv.X > 0f, $"mtv must push out along +X, was {mtv}");
        Vector3 unit = Vector3.Normalize(mtv);
        Assert.True(Vector3.Dot(unit, Vector3.UnitX) > 0.9f, $"mtv direction must be ~+X, was {unit}");
        // Geometric overlap: box face at 0.5, capsule radius 0.4, centre at 0.6 -> penetration 0.4 - (0.6 - 0.5) = 0.3.
        Assert.True(MathF.Abs(mtv.Length() - 0.3f) < 1e-2f,
            $"mtv length must match the geometric overlap (~0.3), was {mtv.Length():F4}");
    }

    // ---- Test 2: triangle-mesh penetration MTV pushes out (nonconvex path) ----
    [Fact]
    public void MeshPenetration_PushesOut()
    {
        // A front-wound (faces toward +X, normal +X) quad wall in the YZ plane at x=0.
        // Winding [0,1,2],[0,2,3] with the vertices below gives an outward normal along +X.
        var verts = new[]
        {
            new Vector3(0f, -1f, -1f), // 0
            new Vector3(0f, -1f,  1f), // 1
            new Vector3(0f,  1f,  1f), // 2
            new Vector3(0f,  1f, -1f), // 3
        };
        var indices = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new TriangleMeshShape(verts, indices);

        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(mesh, Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // Capsule centre just to the +X side of the wall plane, overlapping it (centre at x=0.2, radius 0.4).
        bool overlap = world.ComputePenetration(cap, Pose.At(new Vector3(0.2f, 0f, 0f)), out Vector3 mtv);

        Assert.True(overlap, "capsule overlapping the mesh wall must report penetration (nonconvex path)");
        Assert.True(mtv.X > 0f, $"mesh MTV must push out along +X (front face), was {mtv}");
        Assert.True(mtv.Length() > 0f, $"mesh MTV depth must be positive, was {mtv.Length():F4}");
    }

    // ---- Test 3: capsule on a domed rock top settles (no penetration) and is not trapped ----
    [Fact]
    public void DomedRockTop_SettlesAndMoves()
    {
        // A wide low dome hull (octahedron, half-width 1.5, height 1.0) at origin.
        var points = new[]
        {
            new Vector3( 1.5f, 0f,  0f),
            new Vector3(-1.5f, 0f,  0f),
            new Vector3( 0f, 0f,  1.5f),
            new Vector3( 0f, 0f, -1.5f),
            new Vector3( 0f, 1.0f, 0f), // dome top
            new Vector3( 1.0f, 0f, 1.0f),
            new Vector3(-1.0f, 0f, 1.0f),
            new Vector3( 1.0f, 0f, -1.0f),
            new Vector3(-1.0f, 0f, -1.0f),
        };
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new ConvexHullShape(points), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // Rest the capsule above the dome top: top is at y=1.0, capsule half-height = length/2 + radius = 0.9,
        // so a centre slightly above 1.9 is resting on the top with feet at the dome apex, not penetrating.
        var settled = Pose.At(new Vector3(0f, 1.95f, 0f));
        bool penAtRest = world.ComputePenetration(cap, settled, out _);
        Assert.False(penAtRest, "a capsule resting on the dome top must not report penetration");

        // Now drive a horizontal command across the top for several ticks and assert it actually moves.
        var world2 = world;
        Vector3 from = settled.Position;
        Vector3 cur = from;
        for (int i = 0; i < 8; i++)
        {
            // Use ComputePenetration as the trap detector and a small manual XZ nudge to emulate movement:
            // if the capsule were trapped, every step would be cancelled by a large opposing MTV.
            Vector3 target = cur + new Vector3(0.1f, 0f, 0f);
            if (world2.ComputePenetration(cap, Pose.At(target), out Vector3 mtv))
            {
                target.X += mtv.X;
                target.Z += mtv.Z;
            }
            cur = new Vector3(target.X, from.Y, target.Z);
        }
        Assert.True(cur.X - from.X > 0.3f, $"capsule must move non-trivially across the rock top, moved {cur.X - from.X:F3}");
    }

    // ---- Test 4: platform box blocks walk-through and slides on an angled approach ----
    [Fact]
    public void PlatformBox_BlocksAndSlides()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Box half-extents (3, 0.5, 2.5) at (0, y, 12): near face (toward -Z) at z = 12 - 2.5 = 9.5.
        world.AddStatic(new BoxShape(new Vector3(3f, 0.5f, 2.5f)), Pose.At(new Vector3(0f, 0.5f, 12f)));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);

        // Straight walk from z=8 toward +Z: must stop short of the near face (9.5 - capsule radius).
        Vector3 cur = new Vector3(0f, 0.5f, 8f);
        for (int i = 0; i < 40; i++)
        {
            Vector3 target = cur + new Vector3(0f, 0f, 0.1f);
            if (world.ComputePenetration(cap, Pose.At(target), out Vector3 mtv))
            {
                target.X += mtv.X;
                target.Z += mtv.Z;
            }
            cur = new Vector3(target.X, 0.5f, target.Z);
        }
        Assert.True(cur.Z < 9.5f, $"capsule must not pass through the platform near face, final Z={cur.Z:F3}");

        // Angled approach (toward +Z and +X) must slide laterally along the face, not hard-stop in X.
        Vector3 cur2 = new Vector3(0f, 0.5f, 8f);
        for (int i = 0; i < 40; i++)
        {
            Vector3 target = cur2 + new Vector3(0.07f, 0f, 0.07f);
            if (world.ComputePenetration(cap, Pose.At(target), out Vector3 mtv))
            {
                target.X += mtv.X;
                target.Z += mtv.Z;
            }
            cur2 = new Vector3(target.X, 0.5f, target.Z);
        }
        Assert.True(cur2.X > 0.5f, $"angled approach must slide laterally (X advances), final X={cur2.X:F3}");
    }

    // ---- Test 5: tree -> trunk cylinder bake + KECL round-trip ----
    [Fact]
    public void Tree_BakesTrunkCylinder_RoundTrips()
    {
        GltfMesh tree = FeelMeshes.Tree(trunkRadius: 0.3f, height: 6f, canopyRadius: 2.5f);
        // Sanity: this fixture must classify as a tree (not a walkable solid).
        Assert.True(PropCollisionBake.IsTree(tree), "tree fixture must classify as a tree");

        PhysicsShape shape = PropCollisionBake.Bake(tree);
        var cyl = Assert.IsType<CylinderShape>(shape);

        // Radius ~= the trunk half-extent (small), NOT the canopy width.
        Assert.True(cyl.Radius < 0.6f, $"trunk radius must be ~trunk half-extent (small), was {cyl.Radius:F3}");
        Assert.True(cyl.Radius > 0.2f, $"trunk radius must be non-trivial, was {cyl.Radius:F3}");
        // Length ~= full prop height.
        Assert.True(MathF.Abs(cyl.Length - 6f) < 1e-2f, $"length must be the full height (~6), was {cyl.Length:F3}");

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        ms.Position = 0;
        // Kind byte 3 follows magic (4) + version (1).
        ms.Position = 5;
        Assert.Equal(3, ms.ReadByte());
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var roundTrip = Assert.IsType<CylinderShape>(loaded);
        Assert.Equal(cyl.Radius, roundTrip.Radius, 5);
        Assert.Equal(cyl.Length, roundTrip.Length, 5);
    }

    // ---- Test 6: walk under canopy passes; walk into trunk blocks at trunkRadius + capsuleRadius ----
    [Fact]
    public void Tree_WalkUnderCanopy_VsWalkIntoTrunk()
    {
        GltfMesh tree = FeelMeshes.Tree(trunkRadius: 0.3f, height: 6f, canopyRadius: 2.5f);
        var cyl = (CylinderShape)PropCollisionBake.Bake(tree);
        float trunkRadius = cyl.Radius;

        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Place the cylinder static the runtime way (ChunkStatics.AddAll): at the prop BASE (y=0). The
        // Bepu backend lifts the cylinder +Length/2 so it spans base -> top. Static at origin.
        world.AddStatic(cyl, Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        float capHalfHeight = 0.5f + 0.4f; // length/2 + radius

        // (a) Walk at a canopy-height offset OUTSIDE the trunk radius: free passage. Choose a lateral
        // offset wider than trunk + capsule radius, at a Y where only the canopy would be (above the
        // trunk slice) - the trunk cylinder does not reach laterally that far, so no penetration.
        float canopyY = 4.0f; // well up the trunk, but the cylinder is only trunkRadius wide here
        float clearX = trunkRadius + 0.4f + 0.5f; // safely outside the cylinder + capsule radius
        bool penOutside = world.ComputePenetration(cap, Pose.At(new Vector3(clearX, canopyY, 0f)), out _);
        Assert.False(penOutside, "walking outside the trunk radius (under the canopy) must be free");

        // (b) Walk straight at the trunk at body height: blocked. Approach from -X toward the trunk.
        Vector3 cur = new Vector3(-3f, capHalfHeight, 0f);
        for (int i = 0; i < 60; i++)
        {
            Vector3 target = cur + new Vector3(0.1f, 0f, 0f);
            if (world.ComputePenetration(cap, Pose.At(target), out Vector3 mtv))
            {
                target.X += mtv.X;
                target.Z += mtv.Z;
            }
            cur = new Vector3(target.X, capHalfHeight, target.Z);
        }
        // Must be blocked at ~ -(trunkRadius + capsuleRadius) on the -X side.
        float expectedBlock = -(trunkRadius + 0.4f);
        Assert.True(cur.X < expectedBlock + 0.15f,
            $"walking into the trunk must block near x={expectedBlock:F3}, final X={cur.X:F3}");
        // And the trunk must genuinely block at the correct (low) height: the static cylinder spans
        // base->top, so the capsule at body height collides. If it were half-buried, body-height contact
        // would not stop it here.
        bool blockedAtBody = world.ComputePenetration(cap, Pose.At(new Vector3(0f, capHalfHeight, 0f)), out _);
        Assert.True(blockedAtBody, "capsule centred on the trunk at body height must penetrate (cylinder spans base->top)");
    }

    // ---- Test 7: box and sphere statics still depenetrate via the general path ----
    [Fact]
    public void BoxAndSphere_StillDepenetrate()
    {
        var cap = new CapsuleShape(0.4f, 1.0f);

        using (IPhysicsWorld boxWorld = new BepuPhysicsWorld())
        {
            boxWorld.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(Vector3.Zero));
            boxWorld.Step(1f / 60f);
            // Overlap the +X flank (box face at x=1, capsule centre at x=1.2, radius 0.4 -> overlap 0.2).
            bool boxPen = boxWorld.ComputePenetration(cap, Pose.At(new Vector3(1.2f, 0f, 0f)), out Vector3 boxMtv);
            Assert.True(boxPen, "box static must still depenetrate via the general path");
            Assert.True(boxMtv.X > 0f, $"box MTV must push out along +X, was {boxMtv}");
        }

        using (IPhysicsWorld sphWorld = new BepuPhysicsWorld())
        {
            sphWorld.AddStatic(new SphereShape(1f), Pose.At(Vector3.Zero));
            sphWorld.Step(1f / 60f);
            // Sphere radius 1, capsule radius 0.4, centre at x=1.2 -> combined 1.4 > 1.2 -> overlap 0.2.
            bool sphPen = sphWorld.ComputePenetration(cap, Pose.At(new Vector3(1.2f, 0f, 0f)), out Vector3 sphMtv);
            Assert.True(sphPen, "sphere static must still depenetrate via the general path");
            Assert.True(sphMtv.X > 0f, $"sphere MTV must push out along +X, was {sphMtv}");
        }
    }
}

/// <summary>Hand-authored mesh fixtures for the physics-feel tests.</summary>
static class FeelMeshes
{
    /// <summary>A tree: a thin square trunk from y=0 up to the canopy base, plus a wide canopy box on top.
    /// Tall (> SolidHeightMeters) with a canopy spreading > 1.6x the trunk, so
    /// <see cref="PropSurfaceBake.IsWalkableSolid"/> classifies it as a tree.</summary>
    public static GltfMesh Tree(float trunkRadius, float height, float canopyRadius)
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();

        float canopyBase = height * 0.5f; // canopy occupies the top half
        // Trunk: a square column from y=0 to canopyBase, half-width trunkRadius.
        AddBox(verts, idx, -trunkRadius, trunkRadius, 0f, canopyBase, -trunkRadius, trunkRadius);
        // Canopy: a wide box from canopyBase to height, half-width canopyRadius.
        AddBox(verts, idx, -canopyRadius, canopyRadius, canopyBase, height, -canopyRadius, canopyRadius);

        return new GltfMesh(verts.ToArray(), idx.ToArray());
    }

    static void AddBox(List<ModelVertex> verts, List<uint> idx,
        float x0, float x1, float y0, float y1, float z0, float z1)
    {
        // 8 corners.
        Vector3[] c =
        {
            new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1),
            new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1),
        };
        // 6 faces (12 triangles), outward winding.
        AddQuad(verts, idx, c[0], c[1], c[2], c[3]); // bottom
        AddQuad(verts, idx, c[7], c[6], c[5], c[4]); // top
        AddQuad(verts, idx, c[0], c[4], c[5], c[1]); // -Z
        AddQuad(verts, idx, c[2], c[6], c[7], c[3]); // +Z
        AddQuad(verts, idx, c[3], c[7], c[4], c[0]); // -X
        AddQuad(verts, idx, c[1], c[5], c[6], c[2]); // +X
    }

    static void AddQuad(List<ModelVertex> verts, List<uint> idx,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        uint b0 = (uint)verts.Count;
        verts.Add(V(a)); verts.Add(V(b)); verts.Add(V(c)); verts.Add(V(d));
        idx.Add(b0); idx.Add(b0 + 1); idx.Add(b0 + 2);
        idx.Add(b0); idx.Add(b0 + 2); idx.Add(b0 + 3);
    }

    static ModelVertex V(Vector3 p) => new ModelVertex(p, Vector3.UnitY, Vector4.One);
}
