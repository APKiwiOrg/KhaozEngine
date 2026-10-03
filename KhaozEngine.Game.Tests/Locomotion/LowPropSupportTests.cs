using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>A low flat prop whose top sits within a tenth of a metre of the analytic terrain under it, such as a bridge
/// deck lip over a bank (KhaozEngine #1253). The terrain reads the bank height under the whole prop, so the prop is
/// the only support above it.</summary>
public class LowPropSupportTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;   // walk 6, run 12, radius 0.4, half height 0.9
    static readonly Func<float, float, float> Flat = (_, _) => 0f;
    const float Dt = 1f / 60f;
    const float DeckEdge = 0f;
    const float DeckCentre = 2f;

    [Theory]
    [InlineData(0.025f, false)]
    [InlineData(0.025f, true)]
    [InlineData(0.05f, false)]
    [InlineData(0.05f, true)]
    [InlineData(0.1f, false)]
    [InlineData(0.1f, true)]
    public void CapsuleWalksOntoALowPropTop(float lip, bool run)
    {
        using IPhysicsWorld world = DeckWorld(lip);
        var body = Standing(-1.5f, 0f);

        body = WalkTo(world, body, DeckCentre, run);

        Assert.InRange(body.Position.X, DeckCentre - 0.001f, DeckCentre + 0.001f);
        Assert.InRange(Feet(body), lip - 0.001f, lip + 0.001f);
    }

    [Theory]
    [InlineData(0.025f, false)]
    [InlineData(0.025f, true)]
    [InlineData(0.1f, false)]
    [InlineData(0.1f, true)]
    public void CapsuleWalksOffALowPropTop(float lip, bool run)
    {
        using IPhysicsWorld world = DeckWorld(lip);
        var body = Standing(DeckCentre, lip);

        body = WalkTo(world, body, -1.5f, run);

        Assert.InRange(body.Position.X, -1.501f, -1.499f);
        Assert.InRange(Feet(body), -0.001f, 0.001f);
    }

    [Theory]
    [InlineData(0.025f)]
    [InlineData(0.1f)]
    public void CapsuleSetOnALowPropTopStaysOnIt(float lip)
    {
        using IPhysicsWorld world = DeckWorld(lip);
        var body = Standing(DeckCentre, lip);

        for (int tick = 0; tick < 30; tick++)
        {
            body = CharacterMovement.StepTowards(body, Vector2.Zero, false, Dt, Flat, Tuning, world: world);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}");
            Assert.InRange(Feet(body), lip - 0.001f, lip + 0.001f);
        }
    }

    // Band 2 of the measured guarantee: a radius 4 sphere standing 0.3 m out of the terrain meets it at a 0.925
    // normal, flatter than the low prop test, so the body is seated on its base and walks up to its crown without
    // sinking into it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapsuleWalksUpAGentleMound(bool run)
    {
        (MoveState body, float deepest, _) = WalkToTheCrown(4f, 0.3f, run);

        Assert.InRange(body.Position.X, -0.001f, 0.001f);
        Assert.InRange(Feet(body), 0.298f, 0.302f);
        Assert.True(deepest > -0.01f, $"the capsule sank {-deepest:F3} m into the mound");
    }

    // Band 3: a radius 2 sphere standing 0.3 m out meets the terrain at a 0.85 normal, walkable but steeper than the
    // low prop test. At walk pace the swept move lets the body into the flank, and it is seated once its footprint
    // reaches the near-flat part, so it ends on the crown. This holds for the player capsule at walk pace only. Other
    // capsules and paces can stay sunk in the flank (#1260). How far it sinks first is #1260's, not pinned here.
    [Fact]
    public void CapsuleWalksUpAModerateMoundAtWalkPace()
    {
        (MoveState body, _, _) = WalkToTheCrown(2f, 0.3f, false);

        Assert.InRange(body.Position.X, -0.001f, 0.001f);
        Assert.InRange(Feet(body), 0.298f, 0.302f);
    }

    // Band 1: the radius 2 test dome stands 1 m out and meets the terrain at a 0.5 normal, steeper than the walking
    // slope limit. The body is blocked at its base and never raised.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapsuleIsNeverRaisedUpASteepDome(bool run)
    {
        (MoveState body, _, float highest) = WalkToTheCrown(2f, 1f, run);

        Assert.True(highest <= 0.001f, $"raised to feet {highest:F3} m");
        Assert.True(body.Position.X < -1.5f, $"passed the dome base, centre {body.Position}");
    }

    // A low top tilted to a 0.92 normal, flatter than the low prop test, passes 0.05 m above the terrain under a body
    // at rest. With the default 45 degree slope limit it is walkable and the body is seated on it. With a 20 degree
    // limit (cos 0.94) it is steeper than the walking slope limit, so it is never support, whatever the low prop test.
    [Theory]
    [InlineData(45f, true)]
    [InlineData(20f, false)]
    public void LowTopSteeperThanTheSlopeLimitIsNeverSupport(float slopeDegrees, bool seated)
    {
        MoveTuning tuning = Tuning with { MaxSlopeRadians = slopeDegrees * MathF.PI / 180f };
        using IPhysicsWorld world = new BepuPhysicsWorld();
        float tilt = MathF.Acos(0.92f);
        var normal = new Vector3(-MathF.Sin(tilt), MathF.Cos(tilt), 0f);
        const float HalfThickness = 0.05f;
        world.AddStatic(new BoxShape(new Vector3(1f, HalfThickness, 2f)), new Pose(
            new Vector3(0f, 0.05f, 0f) - normal * HalfThickness, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, tilt)));
        world.Step(Dt);
        var body = new MoveState { Position = new Vector3(0f, tuning.CapsuleHalfHeight, 0f), Grounded = true };

        body = CharacterMovement.StepTowards(body, Vector2.Zero, false, Dt, Flat, tuning, world: world);

        float feet = body.Position.Y - tuning.CapsuleHalfHeight;
        if (seated) Assert.True(feet > 0.055f, $"not seated on the walkable top, feet {feet:F4}");
        else Assert.True(feet < 0.05f, $"seated on a top steeper than the slope limit, feet {feet:F4}");
    }

    // Steers from x -2.5 toward the crown of a sphere standing `height` out of the terrain for 120 ticks, checking
    // the body stays grounded. Returns the final state, the deepest penetration (negative) and the highest feet.
    static (MoveState Body, float Deepest, float Highest) WalkToTheCrown(float radius, float height, bool run)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var centre = new Vector3(0f, height - radius, 0f);
        world.AddStatic(new SphereShape(radius), Pose.At(centre));
        world.Step(Dt);
        var body = Standing(-2.5f, 0f);
        float segment = Tuning.CapsuleHalfHeight - Tuning.CapsuleRadius;
        float speed = run ? Tuning.RunSpeed : Tuning.WalkSpeed;
        float deepest = 0f, highest = 0f;
        for (int tick = 0; tick < 120; tick++)
        {
            float fraction = Math.Clamp(-body.Position.X / (speed * Dt), -1f, 1f);
            body = CharacterMovement.StepTowards(body, new Vector2(fraction, 0f), run, Dt, Flat, Tuning, world: world);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}");
            float closestY = Math.Clamp(centre.Y, body.Position.Y - segment, body.Position.Y + segment);
            float gap = Vector3.Distance(centre, new Vector3(body.Position.X, closestY, body.Position.Z))
                        - radius - Tuning.CapsuleRadius;
            deepest = MathF.Min(deepest, gap);
            highest = MathF.Max(highest, Feet(body));
        }
        return (body, deepest, highest);
    }

    // Steers along X toward the target at the tuning's pace, then holds, checking support every tick.
    static MoveState WalkTo(IPhysicsWorld world, MoveState body, float targetX, bool run)
    {
        float speed = run ? Tuning.RunSpeed : Tuning.WalkSpeed;
        for (int tick = 0; tick < 120; tick++)
        {
            float remaining = targetX - body.Position.X;
            float fraction = Math.Clamp(remaining / (speed * Dt), -1f, 1f);
            body = CharacterMovement.StepTowards(body, new Vector2(fraction, 0f), run, Dt, Flat, Tuning, world: world);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}");
            Assert.True(Feet(body) > -0.001f, $"feet below the terrain at tick {tick}, centre {body.Position}");
        }
        return body;
    }

    static IPhysicsWorld DeckWorld(float lip)
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, lip / 2f, 2f)), Pose.At(new Vector3(DeckEdge + 2f, lip / 2f, 0f)));
        world.Step(Dt);
        return world;
    }

    static MoveState Standing(float x, float feet)
        => new() { Position = new Vector3(x, feet + Tuning.CapsuleHalfHeight, 0f), Grounded = true };

    static float Feet(in MoveState body) => body.Position.Y - Tuning.CapsuleHalfHeight;
}
