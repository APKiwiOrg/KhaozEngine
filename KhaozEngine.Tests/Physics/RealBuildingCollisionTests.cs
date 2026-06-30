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
}
