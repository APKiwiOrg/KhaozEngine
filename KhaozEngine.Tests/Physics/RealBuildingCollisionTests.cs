using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>
/// Regression against a REAL one-sided building collision mesh (a captured Ruinborne house with eaves/overhangs,
/// the shape from the tester screenshots). Synthetic flat-quad fixtures produce ZERO-normal tangent contacts and so do NOT
/// reproduce the real failure - the real building pockets present ANGLED contact normals (e.g. n=(0.90,0.38,-0.20))
/// that route a descending capsule to the plain wall-slide projection, which bled the whole downward component and
/// FROZE the capsule mid-air (the "stuck under the awning / on the wall" pin). The invariant under test: a one-sided
/// mesh can never hold the capsule up against gravity - jumping anywhere around a complex building must never leave
/// the capsule pinned airborne; it always falls back to a real floor.
/// </summary>
public class RealBuildingCollisionTests
{
    const float Scale = 1.5f;   // RuinborneWorld.BuildingScale
    // The live Ruinborne movement feel (MaxSlope 40deg, +50% jump height), so this faithfully matches the alpha.
    static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        MaxSlopeRadians = MathF.PI * 40f / 180f,
        JumpSpeed = MoveTuning.Default.JumpSpeed * MathF.Sqrt(1.5f),
    };
    static float Flat(float x, float z) => 0f;

    static TriangleMeshShape PorchBuilding()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "building_with_eaves.coll");
        var shape = (TriangleMeshShape)PropCollisionFormat.Read(path);
        var v = new Vector3[shape.Vertices.Length];
        for (int i = 0; i < v.Length; i++) v[i] = shape.Vertices[i] * Scale;
        return new TriangleMeshShape(v, shape.Indices);
    }

    // Drive a jumping capsule from a grid of starts around the building, pressing toward its centre, and collect
    // any start that ends PINNED: airborne, elevated, with vertical velocity railing toward terminal (the capsule
    // wants to fall but the resolver won't let its position move).
    static List<string> PinnedStarts(out int total)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(PorchBuilding(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);
        CapsuleShape cap = CharacterMovement.CapsuleFor(Tuning);
        var pinned = new List<string>();
        total = 0;
        for (float sx = -7f; sx <= 7f; sx += 1.0f)
        for (float sz = -7f; sz <= 7f; sz += 1.0f)
        {
            var start = new MoveState { Position = new Vector3(sx, 0.9f, sz), Grounded = true };
            if (world.ComputePenetration(cap, Pose.At(start.Position), out _)) continue;   // skip starts inside the mesh
            total++;
            Vector2 toC = new(-sx, -sz);
            if (toC.LengthSquared() < 1e-3f) toC = new(0, -1);
            toC = Vector2.Normalize(toC);
            float yaw = MathF.Atan2(-toC.X, toC.Y);
            var s = start;
            for (int i = 0; i < 200; i++)
            {
                var cmd = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: yaw, jump: (i % 50 == 0));
                s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Tuning, null, world);
            }
            // Pinned = still airborne, elevated above any reasonable standable height, and railing downward (a
            // genuine fall lands within a metre and ends grounded; a pin sits frozen as vVel runs to -MaxFallSpeed).
            if (!s.Grounded && s.Position.Y > 1.3f && s.VerticalVelocity < -20f)
                pinned.Add($"({sx:F0},{sz:F0})->y={s.Position.Y:F1},vVel={s.VerticalVelocity:F0}");
        }
        return pinned;
    }

    [Fact]
    public void JumpingAroundAComplexBuilding_NeverPinsTheCapsuleMidAir()
    {
        List<string> pinned = PinnedStarts(out int total);
        Assert.True(pinned.Count == 0,
            $"capsule pinned mid-air at {pinned.Count}/{total} jump starts around the building (a one-sided mesh must " +
            $"never hold the capsule up against gravity): {string.Join("  ", pinned)}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Collision-proxy goal metric (8.11.0): a REAL authored blacksmith proxy - a CompoundShape of convex boxes
    // (solid main body, forge, anvil, porch posts; roof/eaves/windows dropped), baked by ke-propbake from a
    // <id>_collision.glb. The structural fix for "players get stuck on detailed building geometry": every collision
    // solid is convex, so there is always a unique shortest exit. The metric: scan a grid of stand/jump/walk spots
    // in and around the proxy and find ~0 WEDGES (a settled spot the capsule can neither walk nor jump out of),
    // while elevated surfaces (building top, furniture) remain standable. Scaled by RuinborneWorld.BuildingScale to
    // match the alpha, exactly like the one-sided-mesh fixture above.
    // ---------------------------------------------------------------------------------------------------------

    static CompoundShape BlacksmithProxy()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "blacksmith_proxy.coll");
        var shape = (CompoundShape)PropCollisionFormat.Read(path);
        return (CompoundShape)PhysicsShapeScale.Uniform(shape, Scale);
    }

    // Drop a capsule at (sx,sz) and let it come to rest (or slide off) over `ticks` ticks.
    static MoveState Settle(IPhysicsWorld world, float sx, float sz, float startY = 9f, int ticks = 220)
    {
        var s = new MoveState { Position = new Vector3(sx, startY, sz), Grounded = false };
        for (int i = 0; i < ticks; i++)
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false),
                                       1f / 60f, Flat, Tuning, null, world);
        return s;
    }

    // A WEDGE = a settled capsule that can move more than 0.5 m away in NONE of 8 compass directions, by walking OR
    // by jump-walking. A convex proxy in the open never wedges: the capsule can always slide/back out.
    static bool IsWedged(IPhysicsWorld world, MoveState settled)
    {
        Vector2 origin = new(settled.Position.X, settled.Position.Z);
        for (int phase = 0; phase < 2; phase++)            // phase 0 = walk, phase 1 = jump-walk
        for (int d = 0; d < 8; d++)
        {
            float yaw = d * MathF.PI / 4f;
            MoveState s = settled;
            for (int i = 0; i < 90; i++)
            {
                bool jump = phase == 1 && i % 45 == 0;
                s = CharacterMovement.Step(s, new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: yaw, jump: jump),
                                           1f / 60f, Flat, Tuning, null, world);
            }
            if (Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), origin) > 0.5f) return false;
        }
        return true;
    }

    [Fact]
    public void ScanningInsideAndAroundTheBlacksmithProxy_FindsNoWedges()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var wedges = new List<string>();
        int total = 0;
        float maxStandY = float.MinValue;
        for (float sx = -7f; sx <= 7f; sx += 0.7f)
        for (float sz = -7f; sz <= 7f; sz += 0.7f)
        {
            MoveState s = Settle(world, sx, sz);
            if (!s.Grounded) continue;                       // never settled here (above a tall thin edge); skip
            total++;
            if (s.Position.Y > maxStandY) maxStandY = s.Position.Y;
            if (IsWedged(world, s)) wedges.Add($"({sx:F1},{sz:F1})->y={s.Position.Y:F1}");
        }

        Assert.True(total > 0, "no settle spots found around the proxy (fixture not loaded?)");
        Assert.True(wedges.Count == 0,
            $"capsule wedged (cannot walk OR jump out) at {wedges.Count}/{total} settled spots in/around the " +
            $"blacksmith proxy (a convex proxy must never wedge): {string.Join("  ", wedges)}");
        // The solid building body + furniture must provide elevated standable surfaces, not pass the capsule through.
        Assert.True(maxStandY > 2.0f,
            $"expected an elevated standable surface on the proxy (building top / furniture), max settled y={maxStandY:F2}");
    }

    [Fact]
    public void StandingOnTheSolidBlacksmithBody_HoldsTheCapsule_NoFallThrough()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Drop onto the main building mass (normalized + scaled centre ~ (1.55, 0)). A solid convex proxy must hold
        // the capsule up on top, not let it sink through to the ground.
        MoveState s = Settle(world, 1.55f, 0.0f);
        Assert.True(s.Grounded, $"capsule should rest on the solid proxy body, ended grounded={s.Grounded} y={s.Position.Y:F2}");
        Assert.True(s.Position.Y > 2.0f, $"the solid body should hold the capsule well above ground, y={s.Position.Y:F2}");
    }
}
