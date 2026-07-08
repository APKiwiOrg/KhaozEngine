using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Headless, fixed-dt tests for dynamic rigid bodies through the <see cref="IPhysicsWorld"/> seam:
/// bodies fall under gravity, rest on statics at the expected height, base-aligned shapes do NOT sink by
/// their centroid, bounce loses height, two identical worlds step bit-identically, and remove/sleep are safe.
/// The rest heights are verified by raycasting DOWN onto the settled body (per the Bepu gotcha), never by
/// trusting a single reported contact.</summary>
public class DynamicBodyTests
{
    const float Dt = 1f / 60f;

    // A large static ground plane (thin box) centred so its TOP surface is at y=0.
    static StaticHandle AddGround(IPhysicsWorld world, float topY = 0f)
        => world.AddStatic(new BoxShape(new Vector3(50f, 0.5f, 50f)), Pose.At(new Vector3(0f, topY - 0.5f, 0f)));

    static void StepMany(IPhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++) world.Step(Dt);
    }

    // ---------------------------------------------------------------------
    // Drop and rest.
    // ---------------------------------------------------------------------

    [Fact]
    public void DroppedBox_FallsUnderGravity_AndRestsOnTheGround()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddGround(world);
        // A 1x1x1 box (half-extent 0.5) dropped from y=5. It should rest with its centre at ~0.5 (base on the ground).
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));

        float startY = world.GetDynamicPose(h).Position.Y;
        StepMany(world, 240); // 4 s: fall + settle

        Pose pose = world.GetDynamicPose(h);
        Assert.True(pose.Position.Y < startY - 1f, $"box must have fallen (start {startY}, now {pose.Position.Y})");
        Assert.True(MathF.Abs(pose.Position.Y - 0.5f) < 0.1f,
            $"box centre must rest at ~0.5 (base on ground), was {pose.Position.Y:F3}");

        // Raycast-down verification per the gotcha: the top of the settled box must be near y=1 over its centre.
        bool hit = world.Raycast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 10f, out RayHit rh);
        Assert.True(hit, "ray must hit the settled box (or the ground)");
        float topY = 5f - rh.Distance;
        Assert.True(topY > 0.9f && topY < 1.1f, $"settled box top must be ~1.0, was {topY:F3}");
    }

    // ---------------------------------------------------------------------
    // Centroid-sink regression (the Bepu gotcha), for a DYNAMIC base-aligned shape.
    // ---------------------------------------------------------------------

    [Fact]
    public void DroppedCylinder_BaseAligned_DoesNotSinkHalfItsHeight()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddGround(world);
        // A cylinder radius 0.5, length 2. Base-aligned means when resting on the ground its base is at y=0 and
        // its TOP is at y=2 - NOT y=1 (which is what a centroid-placed cylinder would give, sunk half its height).
        var cyl = new CylinderShape(0.5f, 2f);
        DynamicBodyHandle h = world.AddDynamic(cyl, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));

        StepMany(world, 300); // 5 s to settle upright

        // The body pose reports the base frame (base-aligned wrapper), so a rested cylinder's pose Y is ~0.
        Pose pose = world.GetDynamicPose(h);
        Assert.True(MathF.Abs(pose.Position.Y) < 0.15f,
            $"base-aligned cylinder pose must rest at ~0 (base on ground), was {pose.Position.Y:F3} (a centroid sink would be ~-1)");

        // Raycast-down over the axis: the top must be ~2, proving the full height sits ABOVE the ground.
        bool hit = world.Raycast(new Vector3(0f, 6f, 0f), -Vector3.UnitY, 12f, out RayHit rh);
        Assert.True(hit, "ray must hit the settled cylinder");
        float topY = 6f - rh.Distance;
        Assert.True(topY > 1.7f, $"cylinder top must be ~2 (full height above ground), was {topY:F3}; a half-sunk cylinder tops out at ~1");
    }

    [Fact]
    public void DroppedConvexHull_BaseAligned_DoesNotSinkByItsCentroid()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddGround(world);
        // A hull whose points span base y=0 to peak y=2, centroid well above 0. Base-aligned resting => top ~2.
        var hull = new ConvexHullShape(new[]
        {
            new Vector3( 0.8f, 0f,  0.8f), new Vector3(-0.8f, 0f,  0.8f),
            new Vector3( 0.8f, 0f, -0.8f), new Vector3(-0.8f, 0f, -0.8f),
            new Vector3( 0.5f, 2f,  0.5f), new Vector3(-0.5f, 2f,  0.5f),
            new Vector3( 0.5f, 2f, -0.5f), new Vector3(-0.5f, 2f, -0.5f),
        });
        DynamicBodyHandle h = world.AddDynamic(hull, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));

        StepMany(world, 300);

        Pose pose = world.GetDynamicPose(h);
        Assert.True(pose.Position.Y > -0.2f,
            $"base-aligned hull must not sink by its centroid (pose Y {pose.Position.Y:F3} would be strongly negative if sunk)");

        bool hit = world.Raycast(new Vector3(0f, 6f, 0f), -Vector3.UnitY, 12f, out RayHit rh);
        Assert.True(hit, "ray must hit the settled hull");
        float topY = 6f - rh.Distance;
        Assert.True(topY > 1.6f, $"hull top must be near ~2 (base on ground), was {topY:F3}");
    }

    // ---------------------------------------------------------------------
    // Restitution / bounce sanity.
    // ---------------------------------------------------------------------

    [Fact]
    public void DroppedSphere_Bounces_AndLosesHeightEachBounce()
    {
        // A bouncy material (restitution) on both the sphere and the ground so contacts restitute.
        var bouncy = new PhysicsMaterial(1f, 0.9f);
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(50f, 0.5f, 50f)), Pose.At(new Vector3(0f, -0.5f, 0f)), bouncy);
        var sphere = new SphereShape(0.3f);
        DynamicBodyHandle h = world.AddDynamic(sphere, Pose.At(new Vector3(0f, 4f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f }, bouncy); // never sleep so it keeps bouncing

        // Detect apexes robustly: the peak height reached during each airborne arc between ground touches.
        // (A per-frame velocity-sign test picks the discretization frame AFTER the true apex and jitters; the
        // per-arc peak is stable.) A ground touch is y below the touch band; the peak of the arc after it is one
        // bounce apex.
        const float restY = 0.3f;              // sphere radius: centre at rest
        const float touchBand = restY + 0.08f; // "in contact" threshold
        var apexes = new System.Collections.Generic.List<float>();
        bool airborne = false;
        float arcPeak = 0f;
        for (int i = 0; i < 900; i++) // 15 s
        {
            world.Step(Dt);
            float y = world.GetDynamicPose(h).Position.Y;
            if (y > touchBand)
            {
                airborne = true;
                if (y > arcPeak) arcPeak = y;
            }
            else if (airborne) // just landed: close out the arc
            {
                apexes.Add(arcPeak);
                airborne = false;
                arcPeak = 0f;
            }
        }

        Assert.True(apexes.Count >= 2, $"the sphere must bounce at least twice (apexes found: {apexes.Count})");
        // Each successive bounce apex must be strictly lower (energy lost per bounce - a real coefficient of restitution).
        for (int i = 1; i < apexes.Count; i++)
            Assert.True(apexes[i] < apexes[i - 1] - 0.02f,
                $"bounce {i} apex {apexes[i]:F3} must be lower than the previous {apexes[i - 1]:F3}");
    }

    // ---------------------------------------------------------------------
    // Determinism: two identical worlds stepped identically produce identical poses.
    // ---------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalWorlds_SteppedIdentically_ProduceIdenticalPoses()
    {
        static (Vector3 pos, Quaternion orient, Vector3 lin) Run()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            AddGround(world);
            // Off-centre spin so orientation actually evolves (a symmetric drop would hide orientation drift).
            var box = new BoxShape(new Vector3(0.4f, 0.6f, 0.5f));
            DynamicBodyHandle h = world.AddDynamic(box, new Pose(new Vector3(0.2f, 4f, -0.1f),
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 0.3f, 0.2f)), 0.5f)),
                new DynamicBodyDescription(1f) { LinearVelocity = new Vector3(0.3f, 0f, 0.1f), AngularVelocity = new Vector3(1f, 0.5f, 0f) });
            StepMany(world, 200);
            Pose p = world.GetDynamicPose(h);
            world.GetDynamicVelocity(h, out Vector3 lin, out _);
            return (p.Position, p.Orientation, lin);
        }

        var a = Run();
        var b = Run();
        // Bit-identical: same binary, same fixed dt, no wall clock or unseeded randomness in the sim path.
        Assert.Equal(a.pos.X, b.pos.X);
        Assert.Equal(a.pos.Y, b.pos.Y);
        Assert.Equal(a.pos.Z, b.pos.Z);
        Assert.Equal(a.orient.X, b.orient.X);
        Assert.Equal(a.orient.Y, b.orient.Y);
        Assert.Equal(a.orient.Z, b.orient.Z);
        Assert.Equal(a.orient.W, b.orient.W);
        Assert.Equal(a.lin.X, b.lin.X);
        Assert.Equal(a.lin.Y, b.lin.Y);
        Assert.Equal(a.lin.Z, b.lin.Z);
    }

    // ---------------------------------------------------------------------
    // Remove mid-flight is safe. Sleep state reachable. Velocity get/set.
    // ---------------------------------------------------------------------

    [Fact]
    public void RemoveDynamic_MidFlight_IsSafe_AndStopsAffectingTheWorld()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddGround(world);
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));

        StepMany(world, 30); // ~0.5 s: still falling, mid-flight
        Assert.True(world.GetDynamicPose(h).Position.Y < 5f && world.GetDynamicPose(h).Position.Y > 0.6f,
            "body should be mid-flight before removal");

        world.RemoveDynamic(h); // must not throw
        StepMany(world, 60);    // stepping after removal must not throw

        // Querying a removed handle now throws (it is no longer a live body).
        Assert.Throws<ArgumentException>(() => world.GetDynamicPose(h));
        // Double remove is a safe no-op.
        world.RemoveDynamic(h);
    }

    [Fact]
    public void SettledBody_ReachesSleep()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddGround(world);
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(0f, 1.2f, 0f)), DynamicBodyDescription.WithMass(1f));

        Assert.True(world.IsAwake(h), "a freshly added falling body should start awake");
        StepMany(world, 600); // 10 s: fall the short distance and settle to sleep
        Assert.False(world.IsAwake(h), "a body at rest must sleep (Bepu deactivates a settled island)");
    }

    [Fact]
    public void SetDynamicVelocity_WakesAndMovesTheBody()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: isolate the velocity effect
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(0f, 0f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        world.SetDynamicVelocity(h, new Vector3(2f, 0f, 0f), Vector3.Zero);
        world.GetDynamicVelocity(h, out Vector3 lin, out _);
        Assert.Equal(2f, lin.X, 3);

        StepMany(world, 60); // 1 s at 2 m/s -> ~2 m in +X
        Assert.True(world.GetDynamicPose(h).Position.X > 1.5f,
            $"body should have travelled along its set velocity, X was {world.GetDynamicPose(h).Position.X:F3}");
    }

    [Fact]
    public void KinematicBody_MassZero_IgnoresGravity_ButMovesByVelocity()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(); // Earth gravity
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        // Mass <= 0 => infinite mass (kinematic): gravity must not move it, but its velocity must.
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(0f, 5f, 0f)),
            new DynamicBodyDescription(0f) { LinearVelocity = new Vector3(1f, 0f, 0f) });

        StepMany(world, 60); // 1 s
        Pose p = world.GetDynamicPose(h);
        Assert.True(MathF.Abs(p.Position.Y - 5f) < 0.01f, $"kinematic body must not fall, Y was {p.Position.Y:F3}");
        Assert.True(p.Position.X > 0.8f, $"kinematic body must move along its velocity, X was {p.Position.X:F3}");
    }
}
