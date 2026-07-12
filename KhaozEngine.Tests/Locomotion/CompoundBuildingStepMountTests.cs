using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Regression for the 10.68.0 stair-run tangent co-pace gate (SteepFaceAhead) misfiring on COMPOUND building-step
// geometry. On two real baked Ruinborne house proxies the entrance doorstep sits close under a wall/wing, so the
// co-pace's forward capsule sweep hits that other hull, misclassifies the single-riser mount as a continuous stair
// run, and throttles the load-bearing seat advance - re-embedding the footprint the monotone-mount block had just
// committed to clear, so the walking capsule buzzes at flat height (y ~0.90) and never mounts the step. 10.67.0
// mounted both fine. The clean inn/single-riser fixtures have a clear path behind the step, so they do not exercise
// the compound case; these drive the real house_1/house_2 proxies at Ruinborne's tuning (walk 3, radius 0.4, 40 deg
// slope, 1.5x placement scale) with the exact approach poses from Ruinborne's BuildingProxyScanTests.
public class CompoundBuildingStepMountTests
{
    const float Dt = 1f / 60f;
    const float Scale = 1.5f;   // the Ruinborne in-world building placement scale

    // Ruinborne's shared movement tuning (RuinborneWorld.MoveTuning): the engine Default with walk/run 3/6, the
    // +50% jump, and the 40 deg slope gate the consumer surfaced the regression at.
    static MoveTuning Tuning => MoveTuning.Default with
    {
        MaxSlopeRadians = MathF.PI * 40f / 180f,
        WalkSpeed = 3f,
        RunSpeed = 6f,
        JumpSpeed = 8f * MathF.Sqrt(1.5f),
    };

    static PhysicsShape Proxy(string id)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", id + "_proxy.coll");
        return PhysicsShapeScale.Uniform(PropCollisionFormat.Read(path), Scale);
    }

    static float Flat(float x, float z) => 0f;

    static MoveState Settle(IPhysicsWorld world, float sx, float sz)
    {
        var s = new MoveState { Position = new Vector3(sx, 22f, sz), Grounded = false };
        for (int i = 0; i < 420; i++)
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false),
                                       Dt, Flat, Tuning, null, world);
        return s;
    }

    // Walk into the door for 4 s at the given yaw (forward faces the door), then release for 1 s, recording the
    // capsule-centre Y each tick. Mirrors BuildingProxyScanTests.EachEntrance_ClimbsToTheDoor_AndStands.
    static float[] WalkInThenRelease(string id, float sx, float sz, float walkYaw)
    {
        var t = Tuning;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(Proxy(id), Pose.At(Vector3.Zero));
        world.Step(Dt);

        MoveState s = Settle(world, sx, sz);
        Assert.True(s.Grounded, $"{id}: approach spot in front of the entrance should be flat ground (y={s.Position.Y:F3})");

        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: walkYaw, jump: false);
        var y = new float[300];
        for (int i = 0; i < 240; i++)
        {
            s = CharacterMovement.Step(s, cmd, Dt, Flat, t, null, world);
            y[i] = s.Position.Y;
        }
        for (int i = 240; i < 300; i++)
        {
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false),
                                       Dt, Flat, t, null, world);
            y[i] = s.Position.Y;
        }
        return y;
    }

    // house_1: 0.25 m doorstep, approached -Z (yaw 0) with the hall wall close behind the step. Expected capsule
    // centre 1.05..1.28 (feet on the ~0.25 m step). The regression stuck it at flat-ground centre ~0.90.
    [Fact]
    public void House1_WalkAtDoor_ClimbsTheStep_NotBuzzAtFlat()
    {
        float[] y = WalkInThenRelease("house_1", 0f, 3.8f, 0f);
        Assert.True(y[^1] > 1.05f && y[^1] < 1.28f,
            $"house_1: walking at the door should mount the 0.25 m step (centre 1.05..1.28), not buzz at flat 0.90: y={y[^1]:F3}");
    }

    // house_2: 0.32 m doorstep UNDER a building wing, approached +Z (yaw PI). Expected capsule centre 1.16..1.32.
    // The overhead wing is exactly the extra hull the co-pace's forward sweep must not misread as a next riser.
    [Fact]
    public void House2_WalkAtDoor_ClimbsTheStep_NotBuzzAtFlat()
    {
        float[] y = WalkInThenRelease("house_2", 0.09f, -3.9f, MathF.PI);
        Assert.True(y[^1] > 1.16f && y[^1] < 1.32f,
            $"house_2: walking at the door should mount the 0.32 m step under the wing (centre 1.16..1.32), not buzz at flat 0.90: y={y[^1]:F3}");
    }

    // The mount must be MONOTONE once engaged: the stall signature is a rise-then-fall buzz (engage the step, get
    // depenetrated back off it, fall to flat). Absence of any backward vertical step from first engagement is the
    // behavioural contract that the seat is not re-embedded by the co-pace throttle.
    [Theory]
    [InlineData("house_1", 0f, 3.8f, 0f)]
    [InlineData("house_2", 0.09f, -3.9f, 3.14159265f)]
    public void CompoundEntrance_MountIsMonotone_NoBuzz(string id, float sx, float sz, float walkYaw)
    {
        float halfH = Tuning.CapsuleHalfHeight;
        float flatY = halfH;   // ~0.90 flat ground
        float[] y = WalkInThenRelease(id, sx, sz, walkYaw);
        int engage = -1;
        for (int i = 0; i < y.Length; i++) if (y[i] > flatY + 0.02f) { engage = i; break; }
        Assert.True(engage >= 0, $"{id}: never engaged the door step (buzzed at flat): final y={y[^1]:F3}");
        for (int i = engage + 1; i < y.Length; i++)
            Assert.True(y[i] >= y[i - 1] - 2e-3f,
                $"{id}: vertical progress went BACKWARDS at tick {i}: {y[i - 1]:F4} -> {y[i]:F4} (the co-pace re-embed buzz).");
    }
}
