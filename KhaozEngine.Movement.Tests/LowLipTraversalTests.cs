using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

/// <summary>KhaozEngine #1253: a 0.3 m capsule stepping from the ground onto a 2.5 cm deck lip whose west edge is x 0.</summary>
public partial class LowLipTraversalTests
{
    // The ground height callback reads 0 under the deck as well as the bank. Before the core's low prop support
    // rule, a grounded body at that height never took the deck top as support. It walked through the lip with its
    // feet at 0, and a body set on the deck dropped back to 0 in one tick.
    private const float DeckTop = 0.025f;
    private const float Row = 0.125f;

    private static readonly MoveTuning Tuning = GroundTraversalProbeTests.Tuning with
    {
        CapsuleRadius = 0.3f,
        CapsuleHalfHeight = 0.75f,
        StepHeight = 0.4f,
    };

    private static readonly PhysicsNavBakeOptions Options = new(-1.5f, -1f, 1.5f, 1f,
        0.25f, 5f, 6f, 0.8f, 128, 512);

    [Theory]
    [InlineData(-0.375f, -0.125f)]
    [InlineData(-0.125f, -0.375f)]
    [InlineData(-0.125f, 0.125f)]
    [InlineData(0.125f, -0.125f)]
    public void BakeAcceptsEdgesBesideTheLip(float fromX, float toX)
    {
        using BepuPhysicsWorld world = LipWorld();
        GroundMoveContext context = Context(world);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 from = Feet(fromX), to = Feet(toX);

        Assert.True(GroundTraversalProbeTests.Probe(context, Tuning, from, to));
        Assert.True(nav.AllowsSegment(from, to));
    }

    [Fact]
    public void RouteCrossesOntoTheDeck()
    {
        using BepuPhysicsWorld world = LipWorld();
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world), Options, _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        NavPath route = nav.Planner.FindPath(Feet(-1.125f), Feet(1.125f), nav.AgentRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, route.Status);
    }

    [Fact]
    public void BodySetOnTheDeckStaysOnIt()
    {
        using BepuPhysicsWorld world = LipWorld();
        var body = new MoveState
        {
            Position = Feet(0.625f) + Vector3.UnitY * Tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1f,
        };

        body = NpcGroundMovement.Hold(body, 1f / 30f, Tuning, Context(world));

        Assert.True(body.Grounded);
        Assert.InRange(body.Position.Y - Tuning.CapsuleHalfHeight, DeckTop - 0.001f, DeckTop + 0.001f);
    }

    [Theory]
    [InlineData(9f)]
    [InlineData(2f)]
    public void LiveBodyMountsTheLipAtWalkPace(float walkSpeed)
    {
        using BepuPhysicsWorld world = LipWorld();

        MoveState body = WalkOntoTheDeck(Context(world), walkSpeed, DeckTop);

        Assert.InRange(body.Position.Y - Tuning.CapsuleHalfHeight, DeckTop - 0.001f, DeckTop + 0.001f);
        Assert.True(body.Position.X > 0.2f, $"stopped at centre {body.Position}");
    }

    // Task 3's lip height sweep: lips that sank the body (0.04 and 0.05 m at 1 m/s, 0.1 m at 9 m/s) and the 0.06 m
    // lip that stalled it at the edge at 1 m/s.
    [Theory]
    [InlineData(0.04f, 1f)]
    [InlineData(0.05f, 1f)]
    [InlineData(0.06f, 1f)]
    [InlineData(0.1f, 9f)]
    public void LiveBodyMountsLowLipsUpToATenthOfAMetre(float lip, float walkSpeed)
    {
        using BepuPhysicsWorld world = LipWorld(lip);

        MoveState body = WalkOntoTheDeck(Context(world), walkSpeed, lip);

        Assert.InRange(body.Position.Y - Tuning.CapsuleHalfHeight, lip - 0.001f, lip + 0.001f);
        Assert.True(body.Position.X > 0.2f, $"stopped at centre {body.Position}");
    }

    // Steers from the bank toward (1.125, Row) at walk pace for 60 ticks, bounded at the target like the probe.
    private static MoveState WalkOntoTheDeck(GroundMoveContext context, float walkSpeed, float lip)
    {
        MoveTuning tuning = Tuning with { WalkSpeed = walkSpeed };
        const float dt = 1f / 30f;
        var target = new Vector2(1.125f, Row);
        var body = new MoveState
        {
            Position = Feet(-1.125f) + Vector3.UnitY * Tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1f,
        };

        for (int tick = 0; tick < 60; tick++)
        {
            Vector2 delta = target - new Vector2(body.Position.X, body.Position.Z);
            float distance = delta.Length();
            Vector2 direction = distance > 0f
                ? delta / distance * MathF.Min(1f, distance / (walkSpeed * dt))
                : Vector2.Zero;
            body = NpcGroundMovement.Step(body, new RangeSteering(direction, RangeMoveStatus.Following),
                false, dt, tuning, context);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}, lip {lip}");
        }
        return body;
    }

    private static BepuPhysicsWorld LipWorld(float lip = DeckTop)
    {
        BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, lip / 2f, 2f)), Pose.At(new Vector3(1f, lip / 2f, 0f)));
        return world;
    }

    private static GroundMoveContext Context(BepuPhysicsWorld world)
        => new((_, _) => 0f, physics: world);

    private static Vector3 Feet(float x) => new(x, x > 0f ? DeckTop : 0f, Row);
}
