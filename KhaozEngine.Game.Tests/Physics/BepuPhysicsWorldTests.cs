using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class BepuPhysicsWorldTests
{
    [Fact]
    public void Raycast_HitsAStaticBox()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit h);
        Assert.True(hit);
        Assert.Equal(4f, h.Distance, 2);                 // 5 - half-depth 1
        Assert.True(Vector3.Dot(h.Normal, -Vector3.UnitZ) > 0.9f);
    }

    [Fact]
    public void SweepCapsule_StopsBeforeAWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 2f, 0.5f)), Pose.At(new Vector3(0f, 1f, 5f)));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        bool hit = world.SweepCapsule(cap, Pose.At(new Vector3(0f, 1f, 0f)), Vector3.UnitZ, 100f, out SweepHit h);
        Assert.True(hit);
        Assert.True(h.Distance > 0f && h.Distance < 5f);  // contacts before the wall plane at z=4.5
    }

    static ConvexHullShape BoxHull(Vector3 c, Vector3 h) => new(new[]
    {
        c + new Vector3(-h.X,-h.Y,-h.Z), c + new Vector3(h.X,-h.Y,-h.Z),
        c + new Vector3(h.X, h.Y,-h.Z),  c + new Vector3(-h.X, h.Y,-h.Z),
        c + new Vector3(-h.X,-h.Y, h.Z), c + new Vector3(h.X,-h.Y, h.Z),
        c + new Vector3(h.X, h.Y, h.Z),  c + new Vector3(-h.X, h.Y, h.Z),
    });

    [Fact]
    public void SweepCapsule_AgainstACompoundOfConvexHulls_HitsWithoutThrowing()
    {
        // A building collision proxy bakes a CompoundShape whose children are ConvexHullShapes. The Bepu factory
        // must add them as FLAT convex leaves, not nested per-hull compounds: a compound-of-compounds makes the
        // broadphase sweep's ComputeBounds throw ("This should only ever be called on convexes"). Two disjoint hull
        // boxes; a sweep into each must register a hit (proving both leaves are flattened into the broadphase).
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(BoxHull(new Vector3(0f, 1f, 5f), new Vector3(1f, 1f, 0.5f)), Pose.At(Vector3.Zero)),
            new CompoundChild(BoxHull(new Vector3(3f, 1f, 5f), new Vector3(1f, 1f, 0.5f)), Pose.At(Vector3.Zero)),
        });
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(compound, Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        bool hit = world.SweepCapsule(cap, Pose.At(new Vector3(0f, 1f, 0f)), Vector3.UnitZ, 100f, out SweepHit h);
        Assert.True(hit, "sweep into a compound-of-hulls must register a hit (no nested-compound bounds throw)");
        Assert.True(h.Distance > 0f && h.Distance < 5f);
        Assert.True(world.SweepCapsule(cap, Pose.At(new Vector3(3f, 1f, 0f)), Vector3.UnitZ, 100f, out _),
            "the second hull leaf must also be in the broadphase");
    }

    [Fact]
    public void ComputePenetration_PushesOutOfAnOverlap()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // capsule centre inside the box -> must report a separating translation
        bool overlap = world.ComputePenetration(cap, Pose.At(new Vector3(0.5f, 0f, 0f)), out Vector3 mtv);
        Assert.True(overlap);
        Assert.True(mtv.Length() > 0f);
    }

    [Fact]
    public void RemoveStatic_StopsHits()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        StaticHandle h = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.RemoveStatic(h);
        world.Step(1f / 60f);
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out _));
    }

    /// <summary>
    /// Repeatedly add a TriangleMeshShape, raycast it (assert hit), remove it, step, and interleave
    /// Box/Sphere adds to churn the pool. Verifies the Mesh buffer-ownership fix: the triangle buffer
    /// must NOT be returned by AddTriangleMesh (the Mesh owns it; RecursivelyRemoveAndDispose returns it).
    /// Without the fix, pool corruption manifests within a few iterations as crashes or wrong raycast results.
    /// </summary>
    [Fact]
    public void TriangleMesh_AddRemoveStep_Repeatedly_NoCorruption()
    {
        // A flat quad (two triangles) forming a 2x2 m floor at z=5.
        // Vertices:
        //   v0 = (-1, -1, 5), v1 = (1, -1, 5), v2 = (1, 1, 5), v3 = (-1, 1, 5)
        // Two triangles: [0,1,2] and [0,2,3]
        var verts = new[]
        {
            new Vector3(-1f, -1f, 5f),
            new Vector3( 1f, -1f, 5f),
            new Vector3( 1f,  1f, 5f),
            new Vector3(-1f,  1f, 5f),
        };
        var indices = new[] { 0, 1, 2,  0, 2, 3 };
        var meshShape = new TriangleMeshShape(verts, indices);

        using IPhysicsWorld world = new BepuPhysicsWorld();

        for (int iter = 0; iter < 50; iter++)
        {
            // Add the triangle mesh and a couple of box/sphere statics to churn the pool.
            var meshHandle = world.AddStatic(meshShape, Pose.At(Vector3.Zero));
            var boxHandle  = world.AddStatic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(10f, 0f, 0f)));
            var sphHandle  = world.AddStatic(new SphereShape(0.3f), Pose.At(new Vector3(-10f, 0f, 0f)));

            world.Step(1f / 60f);

            // Ray from origin along +Z: must hit the quad at z=5.
            bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh);
            Assert.True(hit, $"iter {iter}: raycast should hit the triangle mesh");
            // The face normal should point roughly along -Z (back toward the ray origin).
            Assert.True(Vector3.Dot(rh.Normal, -Vector3.UnitZ) > 0.5f,
                $"iter {iter}: hit normal should face -Z, was {rh.Normal}");
            // Hit distance should be close to 5 m (the z-plane).
            Assert.True(MathF.Abs(rh.Distance - 5f) < 0.5f,
                $"iter {iter}: expected hit near z=5, got distance={rh.Distance:F3}");

            // Sweep capsule toward the mesh: must also hit.
            var cap = new CapsuleShape(0.3f, 0.6f);
            bool swept = world.SweepCapsule(cap, Pose.At(Vector3.Zero), Vector3.UnitZ, 100f, out SweepHit sh);
            Assert.True(swept, $"iter {iter}: sweep should hit the triangle mesh");

            // Remove everything and step to let Bepu's broadphase flush.
            world.RemoveStatic(meshHandle);
            world.RemoveStatic(boxHandle);
            world.RemoveStatic(sphHandle);
            world.Step(1f / 60f);
        }

        // After all iterations the world must be empty: raycast hits nothing.
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out _),
            "world should be empty after all removes");
    }

    /// <summary>
    /// Add a ConvexHullShape (8 points of a 1x1x1 cube) placed at z=5, raycast and sweep-capsule
    /// toward it, and assert hits at the expected approximate distance.
    ///
    /// Note: ConvexHullHelper.CreateShape recenters the hull to its point-cloud centroid.
    /// The discarded first out-parameter is that centroid offset. We supply unit-cube points
    /// centered at the origin so the centroid is (0,0,0) and the static pose sets world position.
    /// </summary>
    [Fact]
    public void ConvexHull_BlocksAndIsHitByQueries()
    {
        // 8 corners of a unit cube centered at origin in local space.
        // ConvexHullHelper.CreateShape recenters to the point centroid (which is the origin here).
        // We place the static at z=5 in world space so the face nearest the origin is at z=4.5.
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
        var hullShape = new ConvexHullShape(points);

        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Place the hull centroid at z=5; nearest face is at z=4.5.
        world.AddStatic(hullShape, Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        // Raycast from origin along +Z: should hit at approximately z=4.5 => distance ~4.5.
        bool rayHit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh);
        Assert.True(rayHit, "raycast should hit the convex hull");
        Assert.True(rh.Distance > 3.5f && rh.Distance < 5.5f,
            $"expected hit near distance 4.5, got {rh.Distance:F3}");

        // Sweep capsule from origin along +Z: should also stop before or at the hull.
        var cap = new CapsuleShape(0.3f, 0.6f);
        bool swept = world.SweepCapsule(cap, Pose.At(Vector3.Zero), Vector3.UnitZ, 100f, out SweepHit sh);
        Assert.True(swept, "sweep should hit the convex hull");
        Assert.True(sh.Distance > 0f && sh.Distance < 5.5f,
            $"expected sweep to contact hull, got distance={sh.Distance:F3}");
    }

    // Issue #145 regression: RayHit.Body must be null for a hit on a dynamic body, never a fabricated static
    // handle. The static is added FIRST (so it legitimately owns seam id 0, BepuPhysicsWorld's shared _nextId
    // counter that AddStatic/AddDynamic both draw from) and placed well past the ray so it is never the actual
    // hit; the dynamic is added second and placed to be hit with QueryFilter.DynamicsOnly. Before the #145 fix,
    // Body was a non-nullable StaticHandle that defaulted to StaticHandle(0), silently aliasing this id-0 static.
    [Fact]
    public void Raycast_OnADynamicBody_ReportsNullBody()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 50f)));
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(0f, 0f, 5f)),
            DynamicBodyDescription.WithMass(1f));
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh, QueryFilter.DynamicsOnly);
        Assert.True(hit, "ray should hit the dynamic box");
        Assert.Null(rh.Body);
    }

    // Sweep-path counterpart of Raycast_OnADynamicBody_ReportsNullBody: SweepHit.Body shares the same seam type
    // and the same ResolveSeamHandle plumbing, so the sweep path needs its own regression coverage.
    [Fact]
    public void SweepCapsule_OnADynamicBody_ReportsNullBody()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 50f)));
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(0f, 0f, 5f)),
            DynamicBodyDescription.WithMass(1f));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.3f, 0.6f);
        bool hit = world.SweepCapsule(cap, Pose.At(Vector3.Zero), Vector3.UnitZ, 100f, out SweepHit sh, QueryFilter.DynamicsOnly);
        Assert.True(hit, "sweep should hit the dynamic box");
        Assert.Null(sh.Body);
    }

    // Issue #143: ResolveSeamHandle must resolve the ACTUAL static that was hit via the new O(1) reverse index,
    // not just whichever entry happens to be found first. Three statics sit off to the sides (ids 0-2); the one
    // on the ray is added LAST (id 3, never id 0), so a resolver that coincidentally always returned the first
    // seam id it saw would fail this the same way it would fail the id-0 aliasing case above.
    [Fact]
    public void Raycast_OnANonFirstStatic_ReportsThatStaticsOwnHandle()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(20f, 0f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(-20f, 0f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 20f, 0f)));
        StaticHandle onTheRay = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh);
        Assert.True(hit);
        Assert.Equal(onTheRay, rh.Body);
    }

    // Issue #143: RemoveStatic must evict the reverse index by the BEPU handle value, not the seam id - the two
    // diverge as soon as a dynamic body has drawn from the shared _nextId counter (forced here by adding a
    // dynamic first), because Bepu allocates static and body handles from separate pools starting at 0. If
    // RemoveStaticEntry keyed the reverse-removal by the wrong number, it would evict a DIFFERENT static's entry
    // (toKeep's) instead of the one actually being removed, breaking toKeep's resolution after the fact.
    [Fact]
    public void Raycast_OnARemainingStatic_StaysResolvableAfterAnotherStaticIsRemoved()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddDynamic(new BoxShape(new Vector3(0.1f, 0.1f, 0.1f)), Pose.At(new Vector3(50f, 50f, 50f)),
            DynamicBodyDescription.WithMass(1f)); // consumes seam id 0, shifting the statics below off Bepu's own id 0
        StaticHandle toRemove = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(20f, 0f, 0f)));
        StaticHandle toKeep = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        world.RemoveStatic(toRemove);
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh);
        Assert.True(hit);
        Assert.Equal(toKeep, rh.Body);
    }
}
