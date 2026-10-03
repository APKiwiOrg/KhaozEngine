using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

/// <summary>A real duck baked aquatic over AquaticProfileTests' steep channel, steered by MoveToRange and stepped by
/// NpcGroundMovement through the Bepu context with its medium and matching ground height.</summary>
public class NpcSwimNavigationAcceptanceTests
{
    private const float Dt = 1f / 30f;
    private const float Range = 0.3f;
    private static readonly MoveTuning Duck = MoveToRangeSwimTests.Duck;
    private static readonly float FloatY = MoveToRangeSwimTests.FloatY;
    private static readonly RouteApproachOptions Swim = new() { SteerWhileSwimming = true };

    // 16 by 10 cells of 0.25 m, wide enough in Z to swim around a deck.
    private static readonly PhysicsNavBakeOptions Options = new(-2f, -1f, 2f, 1.5f, 0.25f, 2f, 5f, 0.8f, 256, 1024)
    {
        SampleWater = true,
    };

    private static readonly Vector3 Start = new(-1.375f, FloatY, 0.125f);
    private static readonly ReachTarget FarBank = ReachTarget.Point(new Vector3(1.375f, FloatY + Duck.CapsuleHalfHeight, 0.125f));

    [Fact]
    public void SwimmingDuckCrossesDeepWaterIntoRange()
    {
        using BepuPhysicsWorld world = AquaticProfileTests.ChannelWorld();

        (RangeMoveStatus status, MoveState body, _) = Cross(world);

        Assert.Equal(RangeMoveStatus.InRange, status);
        Assert.True(body.Swimming);
        Assert.True(ReachGeometry.Within(new MovementBody(body.Position, Duck.CapsuleRadius, Duck.CapsuleHalfHeight), FarBank, Range));
    }

    [Fact]
    public void SwimmingDuckRoutesAroundALowDeck()
    {
        using BepuPhysicsWorld world = AquaticProfileTests.ChannelWorld();
        // At the waterline, across the channel and both shelves, over Z from -1.5 to 0.375: the direct line at Z 0.125
        // is covered and the duck must swim around its end.
        world.AddStatic(new BoxShape(new Vector3(1f, 0.1f, 0.9375f)), Pose.At(new Vector3(0f, 0f, -0.5625f)));

        (RangeMoveStatus status, MoveState body, float widest) = Cross(world);

        Assert.Equal(RangeMoveStatus.InRange, status);
        Assert.True(ReachGeometry.Within(new MovementBody(body.Position, Duck.CapsuleRadius, Duck.CapsuleHalfHeight), FarBank, Range));
        Assert.True(widest > 0.375f + Duck.CapsuleRadius, $"The duck reached z {widest:F3} only, so it never went around the deck.");
    }

    // Drives the duck from Start towards FarBank for up to 600 ticks. Every swimming pose must pass the clearance check.
    private static (RangeMoveStatus Status, MoveState Body, float WidestZ) Cross(BepuPhysicsWorld world)
    {
        GroundMoveContext context = MoveToRangeSwimTests.Channel(world);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options, _ => 0u);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, new GroundProfileOptions { Aquatic = true });
        var mover = new MoveToRange(aquatic, null, Swim);
        MoveState body = MoveToRangeSwimTests.Floating(Start.X, Start.Z);
        float widest = body.Position.Z;
        RangeMoveStatus status = RangeMoveStatus.Following;
        for (int tick = 0; tick < 600; tick++)
        {
            RangeSteering steering = mover.Tick(body, Duck, FarBank, Range, false, Dt, context);
            status = steering.Status;
            if (status == RangeMoveStatus.InRange) break;
            Assert.True(status is RangeMoveStatus.Following, $"Tick {tick} returned {status}.");
            body = NpcGroundMovement.Step(body, steering, false, Dt, Duck, context);
            Assert.True(body.Swimming, $"The duck stopped swimming at tick {tick}.");
            Assert.True(context.SwimClear(body, Duck), $"Tick {tick} put the duck into a static at {body.Position}.");
            widest = System.MathF.Max(widest, body.Position.Z);
        }
        return (status, body, widest);
    }
}
