using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Regression for the paced step-up's embedded-riser mount stall on a REAL baked building-entrance proxy (the
// Ruinborne inn door step: a solid convex box in front of the hall wall, scaled 1.5 like the game places it). At
// the shipped default walk speed the capsule buzzed at flat height and never climbed the ~0.32 m step: on the
// paced step-up's capped (throttled) pose the feet sit embedded inside the solid riser box, so the support feel-fan's
// downward ray STARTS inside the solid and Bepu returns a zero-distance up-normal "floor" hit. That false "supported"
// reading made the validated-cap discriminator KEEP the smooth stair cap for what is really a single deep riser, so
// the mount was depenetrated back off the step every tick. The one-sided-mesh SingleRiserMountTests cannot catch this
// (no solid below the tread to embed the feet); a solid convex proxy is required, so this drives the real inn proxy.
public class InnDoorStepMountTests
{
    const float Dt = 1f / 60f;
    const float Scale = 1.5f;              // the inn's in-world placement scale
    const float DoorStepTopY = 0.316f;    // the door-step riser top in world Y (baked ~0.211 m, scaled 1.5)

    static PhysicsShape InnProxy()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "inn_proxy.coll");
        return PhysicsShapeScale.Uniform(PropCollisionFormat.Read(path), Scale);
    }

    // The shipped default tuning (radius 0.4, walk 6, climb 3.5) with Ruinborne's 40 deg slope gate. The stall
    // reproduces at the engine default 45 deg gate too; 40 deg matches the consumer that surfaced it.
    static MoveTuning Tuning => MoveTuning.Default with { MaxSlopeRadians = MathF.PI * 40f / 180f };

    static float[] DriveY(int ticks)
    {
        var t = Tuning;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(InnProxy(), Pose.At(Vector3.Zero));
        world.Step(Dt);
        float Ground(float x, float z) => 0f;

        var s = new MoveState { Position = new Vector3(0f, 22f, 5.4f), Grounded = false };
        for (int i = 0; i < 420; i++)   // settle onto the flat approach in front of the door
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false),
                                       Dt, Ground, t, null, world);

        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);   // walk at the door (-Z)
        var y = new float[ticks];
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.Step(s, cmd, Dt, Ground, t, null, world);
            y[i] = s.Position.Y;
        }
        return y;
    }

    [Fact]
    public void WalkingAtTheDoor_ClimbsTheStep_NotBuzzAtFlat()
    {
        float halfH = Tuning.CapsuleHalfHeight;
        float flatY = halfH, mountedY = DoorStepTopY + halfH;   // ~0.90 flat, ~1.22 on the step
        float[] y = DriveY(240);
        Assert.True(y[^1] > mountedY - 0.06f,
            $"walking at the inn door never mounted the step (buzzed at flat height): final Y {y[^1]:F3}, " +
            $"expected ~{mountedY:F3} (flat {flatY:F3}).");
    }

    [Fact]
    public void MountIsMonotone_NoBuzz()
    {
        float halfH = Tuning.CapsuleHalfHeight;
        float flatY = halfH;
        float[] y = DriveY(240);
        int engage = -1;
        for (int i = 0; i < y.Length; i++) if (y[i] > flatY + 0.01f) { engage = i; break; }
        Assert.True(engage >= 0, $"never engaged the door step: final Y {y[^1]:F3}");
        // From engagement the centre Y must never DROP: the stall signature is a rise-then-fall buzz (engage the
        // step, lose the tread, fall back to flat). A monotone climb is exactly its absence.
        for (int i = engage + 1; i < y.Length; i++)
            Assert.True(y[i] >= y[i - 1] - 1e-3f,
                $"vertical progress went BACKWARDS at tick {i}: {y[i - 1]:F4} -> {y[i]:F4} (the embedded-riser mount buzz).");
    }
}
