using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeCarryTests
{
    const float Dt = 1f / 30f;
    // The same flat analytic context as MoveToRangeTests.Flat. A context guards against overlapping steps and
    // test classes run in parallel, so this class keeps its own instance.
    static readonly GroundMoveContext Flat = new((_, _) => 0f);
    static readonly RouteApproachOptions Carry = new() { CarryThroughStraightRuns = true };
    static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;
    static float Bound => Tuning.WalkSpeed * Dt;

    [Fact]
    public void StraightRunTravelsTheFullBoundEveryTick()
    {
        Drive drive = Straight(Carry, 120);
        Assert.Equal(RangeMoveStatus.InRange, drive.Steering[^1].Status);
        IReadOnlyList<NavWaypoint> route = drive.Route.Waypoints;
        Assert.Equal(16, route.Count);
        Assert.Equal(new Vector2(0.125f, 0.125f), route[0].Position);
        Assert.Equal(new Vector2(3.875f, 0.125f), route[^1].Position);
        Assert.True(CheckedFullBoundTicks(drive, route[0].Position, 0.00001d) >= 40);
    }

    [Fact]
    public void StraightCellRouteHoldsFullWalkSpeed()
    {
        double carried = Travelled(Straight(Carry, 30), 30);
        double plain = Travelled(Straight(RouteApproachOptions.Default, 30), 30);
        Assert.True(carried >= 1.97d, $"Carried travel {carried:F4} m.");
        Assert.True(plain <= 1.89d, $"Default travel {plain:F4} m.");
    }

    [Fact]
    public void DiagonalRunAtLargeCoordinatesHoldsPace()
    {
        NavSpace space = Surfaces(146f, 146f);
        var start = new Vector2(149.875f, 150.125f);
        var end = new Vector2(152.625f, 152.875f);
        Drive whole = Run(space, Carry, start, end, 120);
        double travelled = Travelled(whole, 30);
        Assert.True(travelled >= 1.94d, $"Diagonal travel {travelled:F4} m.");
        Assert.Equal(RangeMoveStatus.InRange, whole.Steering[^1].Status);
        Assert.Equal(11, whole.Route.Waypoints.Count);
        Assert.Equal(end, whole.Route.Waypoints[^1].Position);
        Assert.True(CheckedFullBoundTicks(whole, whole.Route.Waypoints[0].Position, 0.0001d) >= 40);
    }

    [Fact]
    public void CarryStopsAtAMandatoryCorner()
    {
        // One cell wide L corridor: east along row 16 to the corner cell (24, 16), then north along column 24.
        NavSpace space = Surfaces(-4f, -4f,
            (x, z) => (z == 16 && x is >= 13 and <= 24) || (x == 24 && z is >= 16 and <= 24));
        var corner = new Vector2(2.125f, 0.125f);
        Drive drive = Run(space, Carry, new Vector2(-0.125f, 0.125f), new Vector2(2.125f, 1.625f), 150);
        Assert.Equal(RangeMoveStatus.InRange, drive.Steering[^1].Status);
        IReadOnlyList<NavWaypoint> route = drive.Route.Waypoints;
        int turn = -1;
        for (int i = 0; i < route.Count; i++)
            if (route[i].Position == corner) turn = i;
        Assert.True(turn > 0);
        Assert.False(drive.Route.IsCollinearPassThrough(turn));

        var cells = new HashSet<(int, int)> { Cell(drive.Bodies[0], -4f, -4f) };
        foreach (NavWaypoint waypoint in route)
            cells.Add(Cell(waypoint.Position, -4f, -4f));
        bool landed = false;
        foreach (MoveState body in drive.Bodies)
        {
            Assert.Contains(Cell(body, -4f, -4f), cells);
            landed |= body.Position.X == corner.X && body.Position.Z == corner.Y;
        }
        Assert.True(landed);
    }

    [Fact]
    public void OvershootConsumesPassedWaypointsButStopsAtTheCorner()
    {
        // One tick carries the body from (0.25, 0) past two collinear waypoints and the corner at (0.7, 0) to
        // (0.8, 0). The follower consumes the passed collinear waypoints and keeps the corner, so the next command
        // aims 0.1 m back at it rather than cutting toward (0.7, 0.25). Without the option the index stays on the
        // first passed waypoint at (0.5, 0).
        Assert.Equal(new Vector2(-0.1f, 0f), Overshoot(Carry));
        Assert.Equal(new Vector2(-0.3f, 0f), Overshoot(RouteApproachOptions.Default));
    }

    [Fact]
    public void CallerFollowConfigConsumesPassedWaypointsWithDefaultOptions()
    {
        // The follow half of StrictConfig: a caller's ConsumePassedCollinearWaypoints survives Default options.
        var consume = new PathFollowConfig { ConsumePassedCollinearWaypoints = true };
        Assert.Equal(new Vector2(-0.1f, 0f), Overshoot(RouteApproachOptions.Default, consume));
        Assert.Equal(new Vector2(-0.3f, 0f), Overshoot(RouteApproachOptions.Default, new PathFollowConfig()));
    }

    [Fact]
    public void RefusedCarryFallsBackToTheWaypoint()
    {
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete,
            new(0.25f, 0f), new(0.5f, 0f), new(0.75f, 0f), new(1f, 0f)));
        // Only a carried step asks for a segment that starts short of x = 0.5 and ends beyond it.
        bool refusedCarry = false;
        var mover = new MoveToRange(planner, Space, (from, to) =>
        {
            bool allowed = to.X <= 0.5f + 0.00001f;
            refusedCarry |= !allowed && from.X < 0.5f - 0.00001f;
            return allowed;
        }, null, Carry);
        ReachTarget target = ReachTarget.Point(new(1f, 0.75f, 0f));
        MoveState body = Body();
        for (int tick = 0; tick < 30; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
        }
        Assert.True(refusedCarry);
        Assert.Equal(0.5f, body.Position.X, 0.00001f);
        Assert.Equal(0f, body.Position.Z);
    }

    [Fact]
    public void DefaultOptionsLeaveCommandsUnchanged()
    {
        NavSpace space = Surfaces(-4f, -4f);
        var planner = new GridPathPlanner(space);
        var legacy = new MoveToRange(planner, space, Open);
        var stated = new MoveToRange(planner, space, Open, null, RouteApproachOptions.Default);
        ReachTarget target = ReachTarget.Point(new(3.875f, 0.75f, 0.125f));
        MoveState body = Body(-0.125f, 0.125f);
        for (int tick = 0; tick < 60; tick++)
        {
            RangeSteering a = legacy.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            RangeSteering b = stated.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldDirection.X), BitConverter.SingleToInt32Bits(b.WorldDirection.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldDirection.Y), BitConverter.SingleToInt32Bits(b.WorldDirection.Y));
            body = NpcGroundMovement.Step(body, a, false, Dt, Tuning, Flat);
        }
    }

    [Fact]
    public void NullOptionsAreRejected()
    {
        NavSpace space = Surfaces(-4f, -4f);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => new MoveToRange(new GridPathPlanner(space), space, Open, null, null!)).ParamName);
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world),
            new(-0.875f, -0.875f, 0.875f, 0.875f, 0.25f, 4f, 5f, 0.8f, 128, 512), _ => 0u);
        GroundNavigation navigation = bake.BuildProfile(Tuning, default);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => new MoveToRange(navigation, null, null!)).ParamName);
    }

    sealed record Drive(List<MoveState> Bodies, List<RangeSteering> Steering, NavPath Route);

    internal static NavSpace Surfaces(float originX, float originZ, Func<int, int, bool>? standable = null)
        => NavSpace.Single(NavGrid.FromSurfaces(32, 32, 0.25f, originX, originZ,
            (x, z) => new NavSurfaceSample(standable?.Invoke(x, z) ?? true, 0f, float.PositiveInfinity),
            stepHeight: 0.4f, agentHeight: 1.5f));

    static Drive Straight(RouteApproachOptions options, int ticks)
        => Run(Surfaces(-4f, -4f), options, new(-0.125f, 0.125f), new(3.875f, 0.125f), ticks);

    static Drive Run(NavSpace space, RouteApproachOptions options, Vector2 start, Vector2 end, int ticks)
    {
        var planner = new RecordingPlanner(new GridPathPlanner(space));
        var mover = new MoveToRange(planner, space, Open, null, options);
        ReachTarget target = ReachTarget.Point(new(end.X, 0.75f, end.Y));
        MoveState body = Body(start.X, start.Y);
        var bodies = new List<MoveState> { body };
        var steering = new List<RangeSteering>();
        for (int tick = 0; tick < ticks; tick++)
        {
            RangeSteering next = mover.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            steering.Add(next);
            if (next.Status == RangeMoveStatus.InRange) break;
            Assert.Equal(RangeMoveStatus.Following, next.Status);
            body = NpcGroundMovement.Step(body, next, false, Dt, Tuning, Flat);
            bodies.Add(body);
        }
        return new Drive(bodies, steering, planner.Last ?? throw new InvalidOperationException("No route was planned."));
    }

    // Checks every tick from the one after the body lands on waypoint 0 to the tick before the final ranged
    // approach, and returns how many ticks it checked.
    static int CheckedFullBoundTicks(Drive drive, Vector2 first, double tolerance)
    {
        List<MoveState> bodies = drive.Bodies;
        int landed = bodies.FindIndex(body => Horizontal(body, first) <= 0.00001d);
        Assert.True(landed >= 0, "The body never landed on waypoint 0.");
        int checkedTicks = 0;
        for (int tick = landed; tick + 1 < bodies.Count; tick++)
        {
            if (tick + 1 == bodies.Count - 1 && drive.Steering[^1].Status == RangeMoveStatus.InRange) break;
            double travel = Horizontal(bodies[tick + 1], new Vector2(bodies[tick].Position.X, bodies[tick].Position.Z));
            Assert.True(Math.Abs(travel - Bound) <= tolerance, $"Tick {tick} travelled {travel:F7} m of {Bound:F7} m.");
            checkedTicks++;
        }
        return checkedTicks;
    }

    static double Travelled(Drive drive, int ticks)
    {
        Assert.True(drive.Bodies.Count > ticks);
        return Horizontal(drive.Bodies[ticks], new Vector2(drive.Bodies[0].Position.X, drive.Bodies[0].Position.Z));
    }

    static double Horizontal(in MoveState body, Vector2 point)
    {
        double dx = (double)body.Position.X - point.X, dz = (double)body.Position.Z - point.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    static (int, int) Cell(in MoveState body, float originX, float originZ)
        => Cell(new Vector2(body.Position.X, body.Position.Z), originX, originZ);

    static (int, int) Cell(Vector2 point, float originX, float originZ)
        => ((int)MathF.Floor((point.X - originX) / 0.25f), (int)MathF.Floor((point.Y - originZ) / 0.25f));

    // Returns the commanded horizontal travel in metres on the tick after the overshoot.
    static Vector2 Overshoot(RouteApproachOptions options, PathFollowConfig? follow = null)
    {
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new(0.25f, 0f), new(0.5f, 0f),
            new(0.6f, 0f), new(0.7f, 0f), new(0.7f, 0.25f), new(0.7f, 2f)));
        var mover = new MoveToRange(planner, Space, Open, follow, options);
        ReachTarget target = ReachTarget.Point(new(0.7f, 0.75f, 2f));
        const float dt = 0.2f;
        MoveState body = Body();
        RangeSteering steering = mover.Tick(body, Tuning, target, 0f, false, dt, Flat);
        body = NpcGroundMovement.Step(body, steering, false, dt, Tuning, Flat);
        Assert.Equal(0.25f, body.Position.X, 0.00001f);
        Assert.Equal(RangeMoveStatus.Following, mover.Tick(body, Tuning, target, 0f, false, dt, Flat).Status);
        body = Flat.Step(body, Vector2.UnitX, false, 0.275f, Tuning);
        Assert.Equal(0.8f, body.Position.X, 0.00001f);
        Assert.Equal(0f, body.Position.Z);
        steering = mover.Tick(body, Tuning, target, 0f, false, dt, Flat);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Vector2 travel = steering.WorldDirection * (Tuning.WalkSpeed * dt);
        return new Vector2(MathF.Round(travel.X, 5), MathF.Round(travel.Y, 5));
    }

    internal sealed class RecordingPlanner(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public List<Vector3> Starts { get; } = new();
        public List<NavPath> Plans { get; } = new();
        public NavPath? Last => Plans.Count == 0 ? null : Plans[^1];
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            NavPath path = inner.FindPath(start, goal, agentRadius, budget);
            Starts.Add(start);
            Plans.Add(path);
            return path;
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Range steering must use region queries.");
    }
}
