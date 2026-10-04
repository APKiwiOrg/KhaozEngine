using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
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

    [Fact]
    public void StraightenedSwimRouteKeepsFewerFloatWaypointsAndArrives()
    {
        using BepuPhysicsWorld world = AquaticProfileTests.ChannelWorld();
        GroundMoveContext context = MoveToRangeSwimTests.Channel(world);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options, _ => 0u);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, new GroundProfileOptions { Aquatic = true });
        var both = new RouteApproachOptions { StraightenRoutes = true, SteerWhileSwimming = true };
        _ = new MoveToRange(aquatic, null, both);
        // The same forwarding the profile constructor does, with the raw plans recorded. Diagonal across open water
        // from the near shelf over the channel to the far shelf, so the raw route is a staircase.
        var recorder = new MoveToRangeCarryTests.RecordingPlanner(aquatic.Planner);
        var mover = new MoveToRange(recorder, aquatic.Space, aquatic.AllowsSegment, null, both);
        MoveState body = MoveToRangeSwimTests.Floating(-1.375f, -0.375f);
        ReachTarget target = ReachTarget.Point(new Vector3(1.375f, FloatY + Duck.CapsuleHalfHeight, 1.125f));

        NavPath? straightened = null;
        RangeMoveStatus status = RangeMoveStatus.Following;
        for (int tick = 0; tick < 600; tick++)
        {
            RangeSteering steering = mover.Tick(body, Duck, target, Range, false, Dt, context);
            straightened ??= mover.Straightener!.LastStraightened;
            status = steering.Status;
            if (status == RangeMoveStatus.InRange) break;
            Assert.True(status is RangeMoveStatus.Following, $"Tick {tick} returned {status}.");
            body = NpcGroundMovement.Step(body, steering, false, Dt, Duck, context);
            Assert.True(body.Swimming, $"The duck stopped swimming at tick {tick}.");
            Assert.True(context.SwimClear(body, Duck), $"Tick {tick} put the duck into a static at {body.Position}.");
        }

        Assert.Equal(RangeMoveStatus.InRange, status);
        Assert.NotNull(straightened);
        NavPath raw = recorder.Plans[0];
        Assert.True(straightened.Waypoints.Count < raw.Waypoints.Count,
            $"Kept {straightened.Waypoints.Count} of {raw.Waypoints.Count} raw waypoints.");
        foreach (NavWaypoint waypoint in straightened.Waypoints)
        {
            NavGrid grid = aquatic.Space.Layers[waypoint.Layer];
            (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
            Assert.True(AquaticProfileTests.Floats(aquatic.Footprint.Columns, grid, x, z),
                $"Kept waypoint {waypoint.Position} on layer {waypoint.Layer} is not a float node.");
        }
    }

    [Fact]
    public void WadingDuckSwimsOutAndWadesBackWithoutStallingAtTheShore()
    {
        // The terraced bank of SwimTraversalProbeTests.RampWorld: wading on the terrace at -0.26 (x 3.125), swimming
        // from the terrace at -0.34 (x 4) out to deep water over -0.44 (x 5.375), then back to the terrace at -0.2
        // (x 2.375), whose range is met only by a body standing in the shallows.
        using BepuPhysicsWorld world = SwimTraversalProbeTests.RampWorld();
        GroundMoveContext context = SwimTraversalProbeTests.RampContext(world);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, AquaticProfileTests.Bank, _ => 0u);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, new GroundProfileOptions { Aquatic = true });
        var shelf = new Vector3(3.125f, SwimTraversalProbeTests.RampBed(3.125f), 0.125f);
        ReachTarget deep = ReachTarget.Point(new Vector3(5.375f, FloatY + Duck.CapsuleHalfHeight, 0.125f));
        var inland = new Vector3(2.375f, SwimTraversalProbeTests.RampBed(2.375f), 0.125f);
        ReachTarget back = ReachTarget.Point(inland + Vector3.UnitY * Duck.CapsuleHalfHeight);
        var wading = new MoveState { Position = shelf + Vector3.UnitY * Duck.CapsuleHalfHeight, Grounded = true, SpeedScale = 1f };

        var mover = new MoveToRange(aquatic, null, Swim);
        (RangeMoveStatus outward, MoveState afloat, int outTicks, int outHeld) = Drive(mover, wading, deep, context);
        Assert.Equal(RangeMoveStatus.InRange, outward);
        Assert.True(afloat.Swimming, "The duck reached deep water without swimming.");
        mover.Reset();
        (RangeMoveStatus inward, MoveState ashore, int inTicks, int inHeld) = Drive(mover, afloat, back, context);
        Assert.Equal(RangeMoveStatus.InRange, inward);
        Assert.True(ashore.Grounded && !ashore.Swimming, "The duck came back without standing on the shelf.");
        // A tick may hold while the core lands the body between swimming and wading. A stall would hold far longer.
        Assert.True(outHeld + inHeld <= 4, $"Held {outHeld} ticks out and {inHeld} ticks back.");
        Assert.True(outTicks + inTicks < 300, $"The round trip took {outTicks} and {inTicks} ticks.");

        // Without SteerWhileSwimming the duck stalls once it starts swimming at the shore.
        (RangeMoveStatus stalled, MoveState stuck, _, int stalledHeld) = Drive(new MoveToRange(aquatic), wading, deep, context);
        Assert.Equal(RangeMoveStatus.Suspended, stalled);
        Assert.True(stuck.Swimming);
        Assert.True(stalledHeld > 100, $"Only {stalledHeld} ticks held without the option.");
    }

    // Steps the body towards the target for up to 600 ticks. Returns the last status, the body, the ticks taken and
    // how many ticks were held Suspended.
    private static (RangeMoveStatus Status, MoveState Body, int Ticks, int Held) Drive(MoveToRange mover, MoveState body,
        ReachTarget target, GroundMoveContext context)
    {
        RangeMoveStatus status = RangeMoveStatus.Following;
        int held = 0, tick = 0;
        for (; tick < 600; tick++)
        {
            RangeSteering steering = mover.Tick(body, Duck, target, Range, false, Dt, context);
            status = steering.Status;
            if (status == RangeMoveStatus.InRange) break;
            Assert.True(status is RangeMoveStatus.Following or RangeMoveStatus.Suspended, $"Tick {tick} returned {status}.");
            if (status == RangeMoveStatus.Suspended) held++;
            body = NpcGroundMovement.Step(body, steering, false, Dt, Duck, context);
            if (body.Swimming) Assert.True(context.SwimClear(body, Duck), $"Tick {tick} put the duck into a static at {body.Position}.");
        }
        return (status, body, tick, held);
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
