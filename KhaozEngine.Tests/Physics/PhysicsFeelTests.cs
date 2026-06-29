using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Covers the physics-feel refinement: the general CollisionBatcher depenetration path
/// (replaces the box/sphere-only analytic switch that left the capsule trapped in hulls/meshes), the
/// thin-trunk-cylinder bake + base-aligned placement, the platform box, and the 3D move-and-depenetrate
/// prop resolution in the vertical-physics CharacterMovement.Step overload (replacing the removed horizontal
/// sweep-slide + downward support probe). Test 1 (HullPenetration_PushesOut) LOCKS the contact-normal sign.</summary>
public class PhysicsFeelTests
{
    // CapsuleHalfHeight 0.9 => 1.8 m total; CapsuleRadius 0.4. Flat terrain for the prop-only feel tests.
    static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f, MaxSlopeRadians: 0.9f);
    static float Flat(float x, float z) => 0f;
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

    // ---- Test 3: capsule dropped on a domed rock top settles (no clip) and can move across it ----
    // Drives the REAL CharacterMovement.Step (the 3D move-and-depenetrate path), not a manual nudge.
    [Fact]
    public void DomedRockTop_SettlesAndMoves()
    {
        // A wide low dome hull (octahedron, half-width 1.5, height 1.0) at origin, base at y=0 (on the terrain).
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

        var cap = CharacterMovement.CapsuleFor(Tuning); // radius 0.4, length 1.0 -> half-height 0.9

        // Drop the capsule from above the dome apex (x=z=0) and let gravity settle it onto the top.
        var state = new MoveState { Position = new Vector3(0f, 3.0f, 0f), Grounded = false };
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, idle, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // (a) Settled on the dome top: the capsule rests well above terrain rest (0.9) and does not clip deep.
        // With the FIX-2 settle slop the capsule rests touching the surface (up to ~1 cm residual overlap), so
        // the check is "no deep clip" (residual within the slop band), not exactly-zero penetration.
        Assert.True(state.Grounded, $"capsule must be grounded resting on the dome top, pos={state.Position}");
        Assert.True(state.Position.Y > 1.5f,
            $"capsule must settle ON the dome (elevated above terrain), Y={state.Position.Y:F3}");
        bool penAtRest = world.ComputePenetration(cap, Pose.At(state.Position), out Vector3 restMtv);
        float restOverlap = penAtRest ? restMtv.Length() : 0f;
        Assert.True(restOverlap <= 0.01f + 1e-3f,
            $"a capsule resting on the dome top must only touch (overlap within the settle slop), was {restOverlap:F4}, pos={state.Position}");

        // (b) Drive a horizontal command across the top and assert it actually moves (not stuck).
        float startX = state.Position.X;
        var walk = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 30; i++)
            state = CharacterMovement.Step(state, walk, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(MathF.Abs(state.Position.X - startX) > 0.3f,
            $"capsule must move non-trivially across the rock top, moved {state.Position.X - startX:F3}");
    }

    // ---- Test 4: platform/wall box blocks walk-through and slides on an angled approach ----
    // Drives the REAL CharacterMovement.Step: depenetration push-out yields net tangential progress (slide),
    // never a hard dead-stop.
    [Fact]
    public void PlatformBox_BlocksAndSlides()
    {
        // A tall wall box (half-extents 3, 2, 0.25) at (0, 2, 8): near face (toward -Z) at z = 8 - 0.25 = 7.75.
        // Tall so a body-height capsule hits a vertical (wall) face, not a step-up-able top.
        static IPhysicsWorld MakeWall()
        {
            var w = new BepuPhysicsWorld();
            w.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 2f, 8f)));
            w.Step(1f / 60f);
            return w;
        }

        // Straight walk toward +Z (Move.Y=-1 at yaw=0 => direction +Z): must stop short of the near face.
        using (IPhysicsWorld world = MakeWall())
        {
            var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
            var straight = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
            for (int i = 0; i < 120; i++)
                state = CharacterMovement.Step(state, straight, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            // Near face at z=7.75, capsule radius 0.4 -> stops centre near z=7.35.
            Assert.True(state.Position.Z < 7.55f,
                $"capsule must not pass through the wall near face, final Z={state.Position.Z:F3}");
            Assert.True(MathF.Abs(state.Position.X) < 0.05f,
                $"a straight approach should not drift laterally, final X={state.Position.X:F3}");
        }

        // Angled approach (toward +Z and +X) must slide laterally along the face, not hard-stop in X.
        using (IPhysicsWorld world = MakeWall())
        {
            var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
            // Move.X=+1 (right), Move.Y=-1 (toward +Z): a 45-degree push into the wall.
            var angled = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
            for (int i = 0; i < 120; i++)
                state = CharacterMovement.Step(state, angled, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            Assert.True(state.Position.X > 0.5f,
                $"angled approach must slide laterally (X advances), final X={state.Position.X:F3}");
            Assert.True(state.Position.Z < 7.55f,
                $"angled approach must still be blocked at the wall, final Z={state.Position.Z:F3}");
        }
    }

    // ---- Test 5: tree -> thin trunk HULL bake (rejects foliage outliers) + KECL round-trip ----
    // Regression lock: a conifer-like mesh with a dense thin trunk core PLUS sparse low foliage points spreading
    // far out must bake to ~the trunk core extent (the radial-core filter rejects the outliers), NOT a fat hull.
    // Trees bake a leaning-trunk ConvexHullShape (8.3.0), not a cylinder.
    [Fact]
    public void Tree_BakesTrunkHull_RoundTrips()
    {
        const float trunkCore = 0.25f;
        GltfMesh tree = FeelMeshes.ConiferTree(trunkCore: trunkCore, height: 6f, canopyRadius: 2.5f,
            foliageRadius: 1.2f, foliageCount: 6);
        Assert.True(PropCollisionBake.IsTree(tree), "tree fixture must classify as a tree");

        PhysicsShape shape = PropCollisionBake.Bake(tree);
        var hull = Assert.IsType<ConvexHullShape>(shape);

        // Every hull point stays near the trunk core (corner radius sqrt(2)*0.25 ~= 0.354), well under the foliage
        // radius (1.2): the radial-core filter rejected the foliage outliers. Thin trunk, not a fat foliage hull.
        foreach (Vector3 p in hull.Points)
        {
            float r = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            Assert.True(r < 0.6f, $"trunk hull must reject foliage outliers (~trunk core, not the foliage), r={r:F3}");
        }

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        // Kind byte 1 (convex hull) follows magic (4) + version (1).
        ms.Position = 5;
        Assert.Equal(1, ms.ReadByte());
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var roundTrip = Assert.IsType<ConvexHullShape>(loaded);
        Assert.Equal(hull.Points.Length, roundTrip.Points.Length);
    }

    // ---- Test 6: walk straight into the trunk blocks at ~trunkRadius+capsuleRadius; offset passes freely ----
    // Drives the REAL CharacterMovement.Step against the thin baked trunk cylinder.
    [Fact]
    public void Tree_WalkUnderCanopy_VsWalkIntoTrunk()
    {
        GltfMesh tree = FeelMeshes.ConiferTree(trunkCore: 0.25f, height: 6f, canopyRadius: 2.5f,
            foliageRadius: 1.2f, foliageCount: 6);
        var hull = (ConvexHullShape)PropCollisionBake.Bake(tree);
        // Trunk half-extent in XZ (the face the capsule meets head-on); the hull excludes the foliage outliers.
        float trunkRadius = 0f;
        foreach (Vector3 p in hull.Points)
            trunkRadius = MathF.Max(trunkRadius, MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Z)));
        const float capsuleRadius = 0.4f;

        // Re-confirm the trunk is thin (the fat-trunk regression lock at the movement layer).
        Assert.True(trunkRadius < 0.6f, $"baked trunk extent must be thin, was {trunkRadius:F3}");

        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Place the trunk hull static at the prop BASE (origin), the runtime way.
        world.AddStatic(hull, Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // (a) Walk straight at the trunk: blocked at ~ -(trunkRadius + capsuleRadius) on the -X side.
        // Approach from -X (Move.X=+1 at yaw=0 => direction +X) on flat terrain.
        var blockState = new MoveState { Position = new Vector3(-3f, 0.9f, 0f), Grounded = true };
        var intoTrunk = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            blockState = CharacterMovement.Step(blockState, intoTrunk, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        float expectedBlock = -(trunkRadius + capsuleRadius);
        Assert.True(blockState.Position.X < expectedBlock + 0.15f,
            $"walking into the trunk must block near x={expectedBlock:F3}, final X={blockState.Position.X:F3}");
        Assert.True(blockState.Position.X > expectedBlock - 0.25f,
            $"must not tunnel into the trunk, final X={blockState.Position.X:F3}");

        // (b) Walk along a lane offset well outside (trunkRadius + capsuleRadius): free passage past the tree.
        // Lane at z = trunkRadius + capsuleRadius + 0.5 clears the trunk; walking +X must cross x=0 unobstructed.
        float laneZ = trunkRadius + capsuleRadius + 0.5f;
        var freeState = new MoveState { Position = new Vector3(-3f, 0.9f, laneZ), Grounded = true };
        for (int i = 0; i < 120; i++)
            freeState = CharacterMovement.Step(freeState, intoTrunk, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(freeState.Position.X > 2f,
            $"walking offset (outside the trunk) must pass freely, final X={freeState.Position.X:F3}");
        Assert.True(MathF.Abs(freeState.Position.Z - laneZ) < 0.1f,
            $"offset lane should not be deflected, final Z={freeState.Position.Z:F3} (lane {laneZ:F3})");
    }

    // ---- Test 9: a FAST jump-landing onto a faceted rock settles feet ON the top, not sunk ----
    // The downward support-sweep floor regression lock. A fast one-tick plunge into a convex hull can leave the
    // capsule sunk (deep-penetration depenetration under-reports the depth in the landing tick); the support
    // sweep from above the head is accurate even when sunk and pins the capsule's feet to the hull top.
    [Fact]
    public void JumpLanding_SettlesFeetOnTop_NotSunk()
    {
        // A faceted convex-hull rock, symmetric in Y about its own centre (so the hull's centroid - which Bepu
        // recenters the shape to - sits at the placed pose). Placed at centre y=0.7 with the top facet ring at
        // +0.7 in local space, its world-space top facet is at y=1.4 (base near y=0). Mid ring (widest) and
        // top/bottom facets make it faceted, not a smooth dome.
        const float topY = 1.4f;     // world-space top facet (centre 0.7 + local halfTall 0.7)
        const float halfTall = 0.7f;
        var points = new[]
        {
            // top facet (+halfTall), half-width 0.9
            new Vector3( 0.9f,  halfTall,  0.9f), new Vector3(-0.9f,  halfTall,  0.9f),
            new Vector3( 0.9f,  halfTall, -0.9f), new Vector3(-0.9f,  halfTall, -0.9f),
            // mid ring (widest, faceted shoulders), half-width 1.5
            new Vector3( 1.5f, 0f,  0f), new Vector3(-1.5f, 0f,  0f),
            new Vector3( 0f,   0f,  1.5f), new Vector3( 0f,   0f, -1.5f),
            new Vector3( 1.05f, 0f,  1.05f), new Vector3(-1.05f, 0f,  1.05f),
            new Vector3( 1.05f, 0f, -1.05f), new Vector3(-1.05f, 0f, -1.05f),
            // bottom facet (-halfTall), mirror of the top so the centroid is at the geometric centre
            new Vector3( 0.9f, -halfTall,  0.9f), new Vector3(-0.9f, -halfTall,  0.9f),
            new Vector3( 0.9f, -halfTall, -0.9f), new Vector3(-0.9f, -halfTall, -0.9f),
        };
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new ConvexHullShape(points), Pose.At(new Vector3(0f, 0.7f, 0f)));
        world.Step(1f / 60f);

        // Sanity: a ray straight down the axis confirms the world-space top facet is at ~topY.
        bool rayHit = world.Raycast(new Vector3(0f, 6f, 0f), -Vector3.UnitY, 12f, out RayHit topRay);
        Assert.True(rayHit, "rock top ray must hit");
        Assert.True(MathF.Abs((6f - topRay.Distance) - topY) < 0.05f,
            $"fixture top facet must be at ~{topY}, was {6f - topRay.Distance:F3}");

        // Drop from well above (centre y=4) straight onto the top facet (x=z=0): a fast landing.
        var state = new MoveState { Position = new Vector3(0f, 4f, 0f), Grounded = false };
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, idle, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        float feetY = state.Position.Y - Tuning.CapsuleHalfHeight;
        Assert.True(MathF.Abs(feetY - topY) < 0.1f,
            $"feet must settle on the rock top (~{topY}, not ~0.5 m sunk), feet={feetY:F3}, pos={state.Position}");
        Assert.True(state.Grounded, $"landed capsule must be grounded, pos={state.Position}");
        Assert.Equal(0f, state.VerticalVelocity, 3);
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

    // ---- Test 8: FIX 2 - depenetration settle slop + per-iteration cap ----
    // The resolve loop pushes out only the overlap beyond a ~1 cm slop and never more than the per-iteration cap,
    // so a settled capsule rests touching-but-not-oscillating instead of micro-jittering around the surface.
    [Fact]
    public void Depenetration_SettlesWithinSlop_AndDoesNotJitter()
    {
        // The CharacterMovement constants under test (kept in sync with the resolve loop).
        const float resolveSlop = 0.01f;

        // A tall wall box so a body-height capsule hits a vertical face, not a step-up-able top.
        static IPhysicsWorld MakeWall()
        {
            var w = new BepuPhysicsWorld();
            w.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 2f, 8f)));
            w.Step(1f / 60f);
            return w;
        }
        var cap = CharacterMovement.CapsuleFor(Tuning); // radius 0.4

        // (a) Walk straight into the wall until settled, then confirm the residual overlap is within the slop band
        // (NOT pushed to exactly zero) - the capsule rests touching the surface, leaving up to the slop overlap.
        using (IPhysicsWorld world = MakeWall())
        {
            var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
            var straight = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
            for (int i = 0; i < 240; i++)
                state = CharacterMovement.Step(state, straight, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

            // A settled capsule leaves a residual overlap no deeper than the slop (within a tight numeric band):
            // it is NOT depenetrated to exactly zero (that round-trip is what micro-oscillates).
            bool pen = world.ComputePenetration(cap, Pose.At(state.Position), out Vector3 mtv);
            float residual = pen ? mtv.Length() : 0f;
            Assert.True(residual <= resolveSlop + 1e-3f,
                $"settled residual overlap must be within the slop band, was {residual:F4} (slop {resolveSlop})");

            // (b) No jitter: stepping the settled state again must not move the capsule's XZ measurably (the slop
            // break means an already-settled capsule is not pushed, so it cannot oscillate frame-to-frame).
            Vector3 before = state.Position;
            for (int i = 0; i < 30; i++)
                state = CharacterMovement.Step(state, straight, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            float xzDrift = MathF.Sqrt(
                (state.Position.X - before.X) * (state.Position.X - before.X) +
                (state.Position.Z - before.Z) * (state.Position.Z - before.Z));
            Assert.True(xzDrift < 1e-3f,
                $"a settled capsule must not oscillate (XZ drift over 30 idle-against-wall steps), drifted {xzDrift:F5}");
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

    /// <summary>A conifer-like tree: a thin dense square trunk core PLUS a handful of SPARSE foliage points
    /// (lowest branches) sitting LOW in the trunk slice, spread out at <paramref name="foliageRadius"/>. The
    /// dense trunk corners dominate the bottom slice; the foliage points are the outliers that the old
    /// <c>max(|x|,|z|)</c> trunk bake grabbed (making the trunk far too fat) and that the percentile bake must
    /// reject. Still classifies as a tree (canopy spreads &gt; 1.6x the low slice).</summary>
    public static GltfMesh ConiferTree(float trunkCore, float height, float canopyRadius,
        float foliageRadius, int foliageCount)
    {
        var verts = new List<ModelVertex>();
        var idx = new List<uint>();

        float canopyBase = height * 0.5f;
        // Dense trunk core: a square column from y=0 to canopyBase, half-width trunkCore.
        AddBox(verts, idx, -trunkCore, trunkCore, 0f, canopyBase, -trunkCore, trunkCore);
        // Wide canopy box on top.
        AddBox(verts, idx, -canopyRadius, canopyRadius, canopyBase, height, -canopyRadius, canopyRadius);

        // Sparse low foliage: tiny triangles placed LOW (y in [0.3, 0.9], inside the trunk slice) at
        // foliageRadius from the axis, around the trunk. Few points => outliers, not the dense cluster.
        for (int k = 0; k < foliageCount; k++)
        {
            float ang = MathF.Tau * k / foliageCount;
            float fx = foliageRadius * MathF.Cos(ang);
            float fz = foliageRadius * MathF.Sin(ang);
            float fy = 0.3f + 0.6f * k / MathF.Max(1, foliageCount - 1); // 0.3..0.9, low in the slice
            var a = new Vector3(fx, fy, fz);
            var b = new Vector3(fx + 0.02f, fy, fz);
            var c = new Vector3(fx, fy + 0.02f, fz);
            uint b0 = (uint)verts.Count;
            verts.Add(V(a)); verts.Add(V(b)); verts.Add(V(c));
            idx.Add(b0); idx.Add(b0 + 1); idx.Add(b0 + 2);
        }

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
