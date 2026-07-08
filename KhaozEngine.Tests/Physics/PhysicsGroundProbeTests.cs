using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Tests for the OPT-IN unified-terrain path: once terrain is registered as physics geometry, a game
/// swaps the analytic <see cref="TerrainCollision"/> ground delegates for a <see cref="PhysicsGroundProbe"/> that
/// raycasts the physics world. The controller then resolves terrain, props, and buildings through one world. The
/// analytic delegate path is untouched (its own tests still pass); this file only covers the unified path.</summary>
public class PhysicsGroundProbeTests
{
    const float Dt = 1f / 60f;

    // A flat terrain chunk (surface at a constant height) over [0,size] x [0,size]. Reuses the winding of the
    // production TerrainChunkBuilder so the collision surface is oriented correctly (CCW-from-above).
    static TriangleMeshShape FlatTerrain(float height, float size = 20f, int res = 8)
    {
        int cols = res + 1;
        var verts = new ModelVertex[cols * cols];
        int vi = 0;
        for (int iz = 0; iz <= res; iz++)
        for (int ix = 0; ix <= res; ix++)
            verts[vi++] = new ModelVertex(new Vector3((float)ix / res * size, height, (float)iz / res * size), Vector3.UnitY, Vector4.One);
        var inds = new System.Collections.Generic.List<uint>();
        for (int iz = 0; iz < res; iz++)
        for (int ix = 0; ix < res; ix++)
        {
            uint i0 = (uint)(iz * cols + ix), i1 = (uint)(iz * cols + ix + 1);
            uint i2 = (uint)((iz + 1) * cols + ix), i3 = (uint)((iz + 1) * cols + ix + 1);
            inds.Add(i0); inds.Add(i2); inds.Add(i3);
            inds.Add(i0); inds.Add(i3); inds.Add(i1);
        }
        var mesh = new GltfMesh(verts, inds.ToArray());
        return TerrainChunkCollision.Build(mesh, surfaceVertexCount: verts.Length)!;
    }

    static readonly MoveTuning Tuning = new(WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f, MaxSlopeRadians: 0.9f);

    [Fact]
    public void Probe_ReportsTerrainSurfaceHeight()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(FlatTerrain(height: 12f), Pose.Identity);
        var probe = new PhysicsGroundProbe(world) { ProbeHeight = 100f, ProbeRange = 200f };

        Assert.Equal(12f, probe.Height(10f, 10f), 2);
        Assert.True(probe.Normal(10f, 10f).Y > 0.99f, "flat terrain normal must point up");
    }

    [Fact]
    public void Probe_MissesOffTerrain_ReturnsFallback()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(FlatTerrain(height: 3f), Pose.Identity);
        var probe = new PhysicsGroundProbe(world) { ProbeHeight = 100f, ProbeRange = 200f, FallbackHeight = -50f };

        // (500, 500) is far outside the [0,20] chunk -> no hit -> fallback height, +Y normal.
        Assert.Equal(-50f, probe.Height(500f, 500f), 2);
        Assert.Equal(Vector3.UnitY, probe.Normal(500f, 500f));
    }

    // The unified path: a capsule dropped over the terrain mesh settles on it, driven by the controller using the
    // PhysicsGroundProbe delegates (NOT an analytic TerrainCollision). Terrain is now physics geometry.
    [Fact]
    public void Controller_UnifiedPath_CapsuleRestsOnTerrainMesh()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(FlatTerrain(height: 7f), Pose.Identity);
        world.Step(Dt);
        var probe = new PhysicsGroundProbe(world) { ProbeHeight = 100f, ProbeRange = 200f };

        var state = new MoveState { Position = new Vector3(10f, 12f, 10f), Grounded = false };
        var cmd = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, cmd, Dt, probe.HeightDelegate, Tuning,
                groundNormal: probe.NormalDelegate, world: world);

        // The capsule half-height is 0.9, so its centre rests at terrain + 0.9 = 7.9.
        Assert.True(MathF.Abs(state.Position.Y - 7.9f) < 0.1f,
            $"capsule must rest on the terrain mesh at ~7.9 (surface 7 + halfHeight 0.9), was {state.Position.Y:F3}");
        Assert.True(state.Grounded, "capsule must be grounded on the terrain mesh");
    }

    // A dynamic body (a crate) between the probe height and the terrain must NOT be read as ground: the probe is
    // statics-only by default, so it returns the TERRAIN height under the crate, not the crate's top.
    [Fact]
    public void Probe_IgnoresDynamicBodyBetweenProbeAndTerrain_ReturnsTerrainHeight()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: park the crate in place
        world.AddStatic(FlatTerrain(height: 2f), Pose.Identity);
        // A 1x1x1 crate centred at y=8 over (10,10): a dynamic body sitting well above the terrain.
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(10f, 8f, 10f)),
            DynamicBodyDescription.WithMass(1f));

        var probe = new PhysicsGroundProbe(world) { ProbeHeight = 100f, ProbeRange = 200f };
        // Statics-only default: the crate (dynamic) is ignored, so the probe sees the terrain at y=2, NOT the
        // crate top at ~8.5.
        Assert.Equal(2f, probe.Height(10f, 10f), 2);
        Assert.True(probe.Normal(10f, 10f).Y > 0.99f, "statics-only ground normal is the flat terrain's +Y");
    }

    // Opting in to QueryMobility.All makes the same probe stand on the dynamic crate (crate top ~8.5).
    [Fact]
    public void Probe_WithAllMobility_StandsOnDynamicBody()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(FlatTerrain(height: 2f), Pose.Identity);
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(10f, 8f, 10f)),
            DynamicBodyDescription.WithMass(1f));

        var probe = new PhysicsGroundProbe(world)
        {
            ProbeHeight = 100f, ProbeRange = 200f, GroundMobility = QueryMobility.All,
        };
        // Now the crate is ground: the probe stops at the crate top (~8.5), not the terrain at 2.
        Assert.Equal(8.5f, probe.Height(10f, 10f), 2);
    }

    // The raw seam contract: QueryMobility.All hits the dynamic (nearest), Statics skips it for the terrain,
    // Dynamics hits only the dynamic. This is the filter the ground probe rides on.
    [Fact]
    public void Raycast_QueryMobility_SelectsStaticsVsDynamics()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        // Add the crate FIRST so the terrain's seam handle is not the zero-valued default (which would make the
        // "static hit resolves a seam handle" assertion indistinguishable from the dynamic default).
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(10f, 8f, 10f)),
            DynamicBodyDescription.WithMass(1f));
        StaticHandle terrain = world.AddStatic(FlatTerrain(height: 2f), Pose.Identity);

        var origin = new Vector3(10f, 100f, 10f);
        var down = -Vector3.UnitY;

        // All: nearest hit is the crate top (~8.5).
        Assert.True(world.Raycast(origin, down, 200f, out RayHit all, QueryFilter.All));
        Assert.Equal(8.5f, all.Point.Y, 2);

        // Statics: crate skipped, terrain at y=2, and the hit resolves the terrain's seam handle.
        Assert.True(world.Raycast(origin, down, 200f, out RayHit statics, QueryFilter.StaticsOnly));
        Assert.Equal(2f, statics.Point.Y, 2);
        Assert.Equal(terrain, statics.Body);

        // Dynamics: only the crate is eligible, terrain skipped -> hits the crate top. A dynamic hit carries no
        // static seam handle, so Body is the default (there is no dynamic handle in RayHit).
        Assert.True(world.Raycast(origin, down, 200f, out RayHit dyn, QueryFilter.DynamicsOnly));
        Assert.Equal(8.5f, dyn.Point.Y, 2);
        Assert.Equal(default, dyn.Body);
    }

    // The sweep path shares the same handler seam and must honour the same mobility filter.
    [Fact]
    public void SweepCapsule_QueryMobility_SelectsStaticsVsDynamics()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(10f, 8f, 10f)),
            DynamicBodyDescription.WithMass(1f));
        StaticHandle terrain = world.AddStatic(FlatTerrain(height: 2f), Pose.Identity);

        var capsule = new CapsuleShape(0.2f, 0.4f);
        var start = Pose.At(new Vector3(10f, 100f, 10f));
        var down = -Vector3.UnitY;

        // All: the sweep stops first at the crate (higher up than the terrain).
        Assert.True(world.SweepCapsule(capsule, start, down, 200f, out SweepHit all, QueryFilter.All));
        // Statics: the crate is skipped, so the sweep travels further before hitting the terrain.
        Assert.True(world.SweepCapsule(capsule, start, down, 200f, out SweepHit statics, QueryFilter.StaticsOnly));
        Assert.True(statics.Distance > all.Distance,
            $"statics-only sweep must pass the crate and travel further ({statics.Distance:F2}) than the all sweep ({all.Distance:F2})");
        Assert.Equal(terrain, statics.Body);

        // Dynamics: only the crate is eligible, so the impact distance matches the all sweep (crate is nearest).
        Assert.True(world.SweepCapsule(capsule, start, down, 200f, out SweepHit dyn, QueryFilter.DynamicsOnly));
        Assert.Equal(all.Distance, dyn.Distance, 3);
        Assert.Equal(default, dyn.Body);
    }

    // The horizontal-only overload also drives off the probe delegate (ground-follow via the physics world).
    [Fact]
    public void Controller_HorizontalStep_FollowsTerrainMeshHeight()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(FlatTerrain(height: 5f), Pose.Identity);
        var probe = new PhysicsGroundProbe(world) { ProbeHeight = 100f, ProbeRange = 200f };

        Vector3 pos = new(10f, 0f, 10f);
        var cmd = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: false);
        pos = CharacterMovement.Step(pos, cmd, Dt, probe.HeightDelegate, Tuning);

        // Y is clamped onto the terrain-mesh surface + half-height (5 + 0.9).
        Assert.Equal(5.9f, pos.Y, 2);
    }
}
