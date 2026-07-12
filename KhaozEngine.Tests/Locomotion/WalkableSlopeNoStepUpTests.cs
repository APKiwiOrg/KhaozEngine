using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// COST PIN for the loosened step-up eligibility. Widening the step-up precheck (LipContactShortRiserTests) must NOT
// make a plain walk up a WALKABLE slope start attempting step-ups: a walkable contact is SUPPORT, resolved by the
// walkable-contact branch BEFORE the step-up gate is ever reached, so the loosened gate never sees it and no probe
// sweeps are spent. There is no counter in shipped code (and none is added for the test), so this pins the behavioural
// signature a spurious step-up would leave: a step-up teleports the capsule UP TO a capsule radius FORWARD in a single
// tick to seat its footprint on a ledge. On a walkable slope no such forward jump may occur - every tick advances by
// only the commanded walk step. Climbing the ramp smoothly, with per-tick horizontal advance bounded by the walk step,
// is exactly the absence of any step-up attempt.
public class WalkableSlopeNoStepUpTests
{
    static MoveTuning Consumer => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f };

    const float Grade = 0.5774f;   // tan 30 deg: normal.Y = cos 30 = 0.87, above the 45 deg / 0.71 walk gate

    // A continuous WALKABLE inclined plane (one-sided, up-facing normal) whose surface is Y = -Grade*Z, so it rises
    // toward -Z at ~30 deg. The capsule starts standing ON it (elevated on a prop, so step 4 follows the prop surface)
    // and walks up - a pure walkable-slope ascent, every forward contact resolved by the walkable-contact branch that
    // sits BEFORE the step-up gate.
    static TriangleMeshShape Ramp(float halfX = 20f)
    {
        float zFront = 4f, zBack = -12f;
        var v = new List<Vector3>
        {
            new(-halfX, -Grade * zFront, zFront), new(halfX, -Grade * zFront, zFront),
            new(halfX, -Grade * zBack, zBack), new(-halfX, -Grade * zBack, zBack),
        };
        // Wound so the surface normal faces up-and-back toward the approaching capsule (+Y dominant, +Z tilt).
        var idx = new List<int> { 0, 1, 2, 0, 2, 3 };
        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WalkableSlope_ClimbsSmoothly_NoStepUpForwardTeleport(bool run)
    {
        var t = Consumer;
        float dt = 1f / 30f;
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(Ramp(), Pose.At(Vector3.Zero));
        world.Step(dt);

        // Start standing ON the ramp surface (Y = -Grade*Z + halfH) at Z=-1, so the capsule is already elevated on the
        // prop and step 4 follows the ramp; walking -Z climbs it.
        float startZ = -1f;
        var state = new MoveState { Position = new Vector3(0f, -Grade * startZ + halfH, startZ), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        float walkStep = (run ? t.RunSpeed : t.WalkSpeed) * dt;
        float startY = state.Position.Y;
        for (int i = 0; i < 60; i++)
        {
            Vector3 pre = state.Position;
            state = CharacterMovement.Step(state, cmd, dt, Ground, t, normal, world);
            float advance = MathF.Sqrt(
                (state.Position.X - pre.X) * (state.Position.X - pre.X) +
                (state.Position.Z - pre.Z) * (state.Position.Z - pre.Z));
            // A step-up seats the footprint by jumping UP TO a radius (0.4 m) forward in one tick; a walk step is only
            // walkStep (0.1 m walk / 0.2 m run). A generous margin still sits far below a step-up teleport, so any
            // step-up attempt that fired and mounted here would trip this. None may: the slope is walkable support.
            Assert.True(advance <= walkStep + 0.06f,
                $"tick {i} advanced {advance:F3} m (>{walkStep + 0.06f:F3}) on a WALKABLE slope - a step-up forward " +
                $"teleport fired where the walkable-contact branch should have handled support (run={run}).");
        }

        // And it actually climbed the slope (the scenario is a real walkable ascent, not a wall the capsule never touched).
        Assert.True(state.Position.Y > startY + 0.8f,
            $"the capsule did not climb the walkable ramp (final Y {state.Position.Y:F3}, started {startY:F3}) - scenario void.");
    }
}
