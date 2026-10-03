using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeSwimTests
{
    private const float Dt = 1f / 30f;
    private static readonly RouteApproachOptions Swim = new() { SteerWhileSwimming = true };
    private static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;

    // Duck geometry with walk 2: step 0.2, so the settling band is 0.2 m. MoveToRangeTests.Tuning's step of 0.4 would
    // put a body 0.3 m below its float line inside the band.
    internal static readonly MoveTuning Duck = SwimTraversalProbeTests.Duck with { WalkSpeed = 2f, RunSpeed = 5f };
    internal static readonly float FloatY = 0f - Duck.SwimSurfaceSubmersionFraction * (2f * Duck.CapsuleHalfHeight);

    // Flat analytic bed at -2 under constant deep water with its surface at 0. A context guards against overlapping
    // steps and test classes run in parallel, so this class keeps its own instance.
    private static readonly GroundMoveContext Deep = new((_, _) => -2f, medium: (_, _, feetY) => new MovementMedium(0f, feetY < 0f));

    [Fact]
    public void SwimmingBodyStaysSuspendedWithoutSwimPermission()
    {
        MoveState body = Floating(0f, 0f);
        ReachTarget far = ReachTarget.Point(new Vector3(2f, FloatY, 0f));

        Assert.Equal(RangeMoveStatus.Suspended, Mover(RouteApproachOptions.Default).Tick(body, Duck, far, 0f, false, Dt, Deep).Status);
        Assert.Equal(RangeMoveStatus.Suspended, Legacy().Tick(body, Duck, far, 0f, false, Dt, Deep).Status);
        Assert.Equal(RangeMoveStatus.Following, Mover(Swim).Tick(body, Duck, far, 0f, false, Dt, Deep).Status);
    }

    [Fact]
    public void AirborneBodyStaysSuspendedWithSwimPermission()
    {
        var body = new MoveState { Position = new Vector3(0f, 1f, 0f), SpeedScale = 1f };

        RangeSteering far = Mover(Swim).Tick(body, Duck, ReachTarget.Point(new Vector3(2f, 1f, 0f)), 0f, false, Dt, Deep);
        RangeSteering near = Mover(Swim).Tick(body, Duck, ReachTarget.Point(body.Position), 0.5f, false, Dt, Deep);

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), far);
        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), near);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedBodyStaysSuspendedWithSwimPermission(bool swimming)
    {
        MoveState body = swimming ? Floating(0f, 0f) : Body();
        body.Commitment = new MovementCommitment { Phase = MovementCommitmentPhase.Recovering };
        GroundMoveContext context = swimming ? Deep : Flat;
        MoveTuning tuning = swimming ? Duck : Tuning;

        RangeSteering near = Mover(Swim).Tick(body, tuning, ReachTarget.Point(body.Position), 0.5f, false, Dt, context);
        RangeSteering far = Mover(Swim).Tick(body, tuning, ReachTarget.Point(new Vector3(2f, body.Position.Y, 0f)), 0f,
            false, Dt, context);

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), near);
        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), far);
    }

    [Fact]
    public void SettlingSwimmerIsSuspendedUntilItReachesTheFloatBand()
    {
        // A body that fell into deep water: 0.3 m below its float line and still sinking.
        MoveState body = Floating(0f, 0f, FloatY - 0.3f);
        body.VerticalVelocity = -1f;
        MoveToRange mover = Mover(Swim);
        ReachTarget far = ReachTarget.Point(new Vector3(2f, FloatY, 0f));

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended),
            Mover(Swim).Tick(body, Duck, ReachTarget.Point(body.Position), 0.5f, false, Dt, Deep));
        int held = 0;
        RangeSteering steering = mover.Tick(body, Duck, far, 0f, false, Dt, Deep);
        while (steering.Status == RangeMoveStatus.Suspended && held < 120)
        {
            Assert.Equal(Vector2.Zero, steering.WorldDirection);
            Assert.True(MathF.Abs(Feet(body, Duck).Y - FloatY) > Duck.StepHeight, $"Suspended inside the band at tick {held}.");
            body = NpcGroundMovement.Step(body, steering, false, Dt, Duck, Deep);
            held++;
            steering = mover.Tick(body, Duck, far, 0f, false, Dt, Deep);
        }

        Assert.True(held > 0, "The settling body was never suspended.");
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.True(MathF.Abs(Feet(body, Duck).Y - FloatY) <= Duck.StepHeight);
    }

    [Fact]
    public void SwimmerWithoutWaterAtItsFeetIsSettling()
    {
        MoveState body = Floating(0f, 0f);
        ReachTarget far = ReachTarget.Point(new Vector3(2f, FloatY, 0f));
        var noMedium = new GroundMoveContext((_, _) => -2f);
        var dryFeet = new GroundMoveContext((_, _) => -2f, medium: (_, _, _) => MovementMedium.Dry);

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), Mover(Swim).Tick(body, Duck, far, 0f, false, Dt, noMedium));
        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended), Mover(Swim).Tick(body, Duck, far, 0f, false, Dt, dryFeet));
        Assert.Equal(RangeMoveStatus.Following, Mover(Swim).Tick(body, Duck, far, 0f, false, Dt, Deep).Status);
    }

    [Fact]
    public void SwimmerCarriesThroughStraightRunsAtSwimPace()
    {
        // Waypoints every 0.2 m on a straight line from the start: without carry the swimmer lands on each one, short
        // of a full bound. Waypoint 0 is the start, because carry never passes waypoint 0.
        Vector2[] points = [.. Enumerable.Range(0, 16).Select(i => new Vector2(0.2f * i, 0f))];
        float bound = Duck.SwimSpeed * Dt;
        float carried = SwimTravel(new RouteApproachOptions { SteerWhileSwimming = true, CarryThroughStraightRuns = true }, points);
        float plain = SwimTravel(Swim, points);

        Assert.InRange(carried, 12f * bound - 1e-4f, 12f * bound + 1e-4f);
        Assert.True(plain < carried - 0.05f, $"Plain swim travelled {plain:F4} m against {carried:F4} m carried.");
    }

    [Fact]
    public void PermittedSwimRefusesAnAirborneExitStep()
    {
        // Inside the band but 0.15 m above the float line, too shallow to keep swimming: the core takes the land path
        // over a bed 1.85 m below and the prediction is airborne and not swimming.
        MoveState body = Floating(0f, 0f, FloatY + 0.15f);
        MoveState predicted = Deep.Step(body, Vector2.UnitX, false, Dt, Duck);
        Assert.False(predicted.Grounded);
        Assert.False(predicted.Swimming);

        RangeSteering steering = Mover(Swim).Tick(body, Duck, ReachTarget.Point(new Vector3(2f, FloatY, 0f)), 0f, false, Dt, Deep);

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Following), steering);
    }

    [Fact]
    public void SwimTravelBoundCapsTheWaypointAtSwimSpeedAboveWalk()
    {
        Assert.True(Duck.SwimSpeed > Duck.WalkSpeed);
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new(0.05f, 0f), new(2f, 0f)));
        var mover = new MoveToRange(planner, Space, Open, null, Swim);
        MoveState body = Floating(0f, 0f);

        RangeSteering steering = mover.Tick(body, Duck, ReachTarget.Point(new Vector3(2f, FloatY, 0f)), 0f, false, Dt, Deep);
        MoveState after = NpcGroundMovement.Step(body, steering, false, Dt, Duck, Deep);

        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.True(after.Swimming);
        Assert.InRange(after.Position.X, 0.05f - 1e-5f, 0.05f + 1e-5f);
        Assert.InRange(after.Position.Z, -1e-5f, 1e-5f);
    }

    [Fact]
    public void SwimPermissionRequiresAnAquaticProfile()
    {
        using BepuPhysicsWorld world = AquaticProfileTests.ChannelWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(Channel(world), AquaticProfileTests.Wet, _ => 0u);
        GroundNavigation ground = capture.BuildProfile(Duck, default);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, new GroundProfileOptions { Aquatic = true });

        Assert.Equal("options", Assert.Throws<ArgumentException>(() => new MoveToRange(ground, null, Swim)).ParamName);
        _ = new MoveToRange(aquatic, null, Swim);
        _ = new MoveToRange(ground, null, RouteApproachOptions.Default);
        Assert.False(RouteApproachOptions.Default.SteerWhileSwimming);
    }

    [Fact]
    public void DefaultOptionsLeaveSwimmingBodiesSuspended()
    {
        using BepuPhysicsWorld world = AquaticProfileTests.ChannelWorld();
        GroundMoveContext context = Channel(world);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, AquaticProfileTests.Wet, _ => 0u);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, new GroundProfileOptions { Aquatic = true });
        MoveState body = Floating(-1.375f, 0.125f);
        ReachTarget far = ReachTarget.Point(new Vector3(1.375f, FloatY, 0.125f));

        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended),
            new MoveToRange(aquatic).Tick(body, Duck, far, 0f, false, Dt, context));
        Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.Suspended),
            new MoveToRange(aquatic, null, RouteApproachOptions.Default).Tick(body, Duck, far, 0f, false, Dt, context));
        Assert.Equal(RangeMoveStatus.Following, new MoveToRange(aquatic, null, Swim).Tick(body, Duck, far, 0f, false, Dt, context).Status);
    }

    // Horizontal travel of a swimmer after 12 ticks along a scripted straight route.
    private static float SwimTravel(RouteApproachOptions options, Vector2[] points)
    {
        var mover = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, points)), Space, Open, null, options);
        ReachTarget end = ReachTarget.Point(new Vector3(points[^1].X, FloatY, points[^1].Y));
        MoveState body = Floating(0f, 0f);
        for (int tick = 0; tick < 12; tick++)
        {
            RangeSteering steering = mover.Tick(body, Duck, end, 0f, false, Dt, Deep);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Duck, Deep);
            Assert.True(body.Swimming);
        }
        return new Vector2(body.Position.X, body.Position.Z).Length();
    }

    internal static MoveState Floating(float x, float z, float? feetY = null) => new()
    {
        Position = new Vector3(x, (feetY ?? FloatY) + Duck.CapsuleHalfHeight, z),
        Swimming = true,
        SpeedScale = 1f,
    };

    internal static GroundMoveContext Channel(BepuPhysicsWorld world) => new(AquaticProfileTests.ChannelGround,
        physics: world, medium: AquaticProfileTests.ChannelMedium);

    private static MoveToRange Mover(RouteApproachOptions options)
        => new(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(2f, 0f))), Space, Open, null, options);

    private static MoveToRange Legacy()
        => new(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(2f, 0f))), Space, Open);
}
