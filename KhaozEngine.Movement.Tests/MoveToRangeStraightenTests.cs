using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeCarryTests;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeStraightenTests
{
    const float Dt = 1f / 30f;
    const double TurnTolerance = 0.0001d;
    const double TravelTolerance = 0.0001d;
    // A context guards against overlapping steps and test classes run in parallel, so this class keeps its own.
    static readonly GroundMoveContext Flat = new((_, _) => 0f);
    static readonly RouteApproachOptions Straighten = new() { StraightenRoutes = true };
    static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;
    static float Bound => Tuning.WalkSpeed * Dt;

    // The staircase fixture of MoveToRangeCornerTickTests: cell (2, 2) to cell (26, 14) at 146 m coordinates.
    internal static readonly Vector2 StairStart = new(146.625f, 146.625f);
    internal static readonly Vector2 StairEnd = new(152.625f, 149.625f);

    [Fact]
    public void DefaultOptionsLeaveCommandsUnchanged()
    {
        NavSpace space = Surfaces(146f, 146f);
        var legacyPlanner = new RecordingPlanner(new GridPathPlanner(space));
        var statedPlanner = new RecordingPlanner(new GridPathPlanner(space));
        var legacy = new MoveToRange(legacyPlanner, space, Open);
        var stated = new MoveToRange(statedPlanner, space, Open, null, new RouteApproachOptions { StraightenRoutes = false });
        // The control: the same comparison sees a straightening mover on this fixture, so the equality above can fail.
        var straight = new MoveToRange(new GridPathPlanner(space), space, Open, null, Straighten);
        ReachTarget target = Point(StairEnd);
        MoveState body = Body(StairStart.X, StairStart.Y);
        MoveState straightBody = body;
        int differing = 0;
        for (int tick = 0; tick < 120; tick++)
        {
            RangeSteering a = legacy.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            RangeSteering b = stated.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            RangeSteering c = straight.Tick(straightBody, Tuning, target, 0f, false, Dt, Flat);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldDirection.X), BitConverter.SingleToInt32Bits(b.WorldDirection.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldDirection.Y), BitConverter.SingleToInt32Bits(b.WorldDirection.Y));
            if (!SameBits(a.WorldDirection, c.WorldDirection)) differing++;
            body = NpcGroundMovement.Step(body, a, false, Dt, Tuning, Flat);
            straightBody = NpcGroundMovement.Step(straightBody, c, false, Dt, Tuning, Flat);
        }
        Assert.Equal(legacyPlanner.Plans.Count, statedPlanner.Plans.Count);
        Assert.Null(stated.Straightener);
        Assert.True(differing > 0, "a straightening mover steered the same as the default on every tick");
    }

    [Fact]
    public void OffAxisWalkTurnsOnlyAtKeptBends()
    {
        NavSpace space = Surfaces(146f, 146f);
        var drive = new Driver(space, Open, Straighten, StairStart);

        Assert.True(drive.RunTo(StairEnd, 200));

        Assert.True(drive.Recorder.Plans[0].Waypoints.Count >= 13, $"raw has {drive.Recorder.Plans[0].Waypoints.Count}");
        IReadOnlyList<Vector2> bends = Bends(space, drive.Recorder.Plans[0], StairStart, Open);
        AssertTurnsOnlyAtBends(drive, bends);
        double straight = Vector2.Distance(StairStart, StairEnd);
        int limit = (int)Math.Ceiling(straight / Bound) + 2;
        Assert.True(drive.Steering.Count - 1 <= limit, $"InRange on tick {drive.Steering.Count - 1} of {limit}");
    }

    [Fact]
    public void HeadingChangesOnceAtEachKeptCornerOfAConvexObstacle()
    {
        // The obstacle of RouteStraightenerTests.ConvexObstacleKeepsItsCorner. It is four cells wide, so the route
        // keeps both corners on the side it passes.
        Func<int, int, bool> standable = (x, z) => x < 10 || x > 13 || z < 5 || z > 11;
        NavSpace space = Surfaces(-4f, -4f, standable);
        var guard = new RouteStraightenerTests.Sampled(standable, -4f, -4f);
        Vector2 start = CellCentre(-4f, 4, 8), end = CellCentre(-4f, 20, 8);
        var drive = new Driver(space, guard.Allows, Straighten, start);

        Assert.True(drive.RunTo(end, 200));

        IReadOnlyList<Vector2> bends = Bends(space, drive.Recorder.Plans[0], start, guard.Allows);
        Assert.Equal(2, bends.Count);
        Assert.Equal(2, AssertTurnsOnlyAtBends(drive, bends));
        Assert.Single(drive.Recorder.Plans);
    }

    [Fact]
    public void RefusedStraightStepFallsBackForOnePlanAndArrives()
    {
        Driver drive = FallBackDrive(out SeenCells seen);

        Assert.True(seen.Refusals > 0);
        int refused = drive.Steering.FindIndex(s => s.WorldDirection == Vector2.Zero);
        Assert.InRange(refused, 1, 10);
        Assert.Equal(RangeMoveStatus.Following, drive.Steering[refused].Status);
        Assert.Equal(1, drive.PlanCounts[refused]);
        Assert.Equal(2, drive.PlanCounts[refused + 1]);
        Assert.Equal(2, drive.Recorder.Plans.Count);
        Assert.Null(drive.Mover.Straightener!.LastStraightened);
        // StopAtRange takes the body into range before it reaches the waypoints nearest the target.
        int from = refused + 1;
        foreach (NavWaypoint waypoint in drive.Recorder.Plans[1].Waypoints)
        {
            if (Vector2.Distance(waypoint.Position, StairEnd) <= Tuning.CapsuleRadius + Bound) continue;
            int landed = drive.Bodies.FindIndex(from, body => Xz(body) == waypoint.Position);
            Assert.True(landed >= from, $"never landed on {waypoint.Position} after body {from}");
            from = landed;
        }
    }

    [Fact]
    public void StraighteningResumesOnThePlanAfterTheFallback()
    {
        Driver drive = FallBackDrive(out _);
        var moved = new Vector2(148.125f, 152.625f);

        Assert.True(drive.RunTo(moved, 300));

        Assert.Equal(3, drive.Recorder.Plans.Count);
        Assert.NotNull(drive.Mover.Straightener!.LastStraightened);
    }

    [Fact]
    public void RefusedRawStepKeepsTheRoute()
    {
        // Review Focus 2: a refused step on the raw route holds without replanning again.
        NavSpace space = Surfaces(146f, 146f);
        bool armed = false;
        var drive = new Driver(space, (_, _) => !armed, Straighten, StairStart);
        ReachTarget target = Point(StairEnd);
        Assert.Equal(RangeMoveStatus.Following, drive.Mover.Tick(drive.Body, Tuning, target, 0f, false, Dt, Flat).Status);
        Assert.NotNull(drive.Mover.Straightener!.LastStraightened);
        armed = true;

        for (int tick = 0; tick < 30; tick++)
        {
            RangeSteering steering = drive.Tick(StairEnd);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            Assert.Equal(Vector2.Zero, steering.WorldDirection);
        }

        Assert.Equal(2, drive.Recorder.Plans.Count);
        Assert.Null(drive.Mover.Straightener.LastStraightened);
        Assert.Equal(StairStart, Xz(drive.Body));
    }

    [Fact]
    public void CarryAndStraighteningArriveTogether()
    {
        // The L corridor of MoveToRangeCarryTests.CarryStopsAtAMandatoryCorner.
        Func<int, int, bool> standable = (x, z) => (z == 16 && x is >= 13 and <= 24) || (x == 24 && z is >= 16 and <= 24);
        NavSpace space = Surfaces(-4f, -4f, standable);
        var guard = new RouteStraightenerTests.Sampled(standable, -4f, -4f);
        var corner = new Vector2(2.125f, 0.125f);
        var start = new Vector2(-0.125f, 0.125f);
        var drive = new Driver(space, guard.Allows,
            new RouteApproachOptions { StraightenRoutes = true, CarryThroughStraightRuns = true }, start);

        Assert.True(drive.RunTo(new Vector2(2.125f, 1.625f), 150));

        IReadOnlyList<Vector2> bends = Bends(space, drive.Recorder.Plans[0], start, guard.Allows);
        Assert.Equal(new[] { corner }, bends);
        Assert.Contains(drive.Bodies, body => Xz(body) == corner);
        Assert.Equal(1, AssertTurnsOnlyAtBends(drive, bends));
        Assert.Single(drive.Recorder.Plans);
    }

    // Walks the staircase fixture to arrival under a guard that, once armed after the first plan, refuses a segment
    // with any sampled point in a cell no raw plan's polyline crosses. The straight line soon leaves the first raw
    // route. A caller in another class passes its own context, since contexts refuse overlapping steps.
    internal static Driver FallBackDrive(out SeenCells seen, RouteApproachOptions? options = null,
        GroundMoveContext? context = null)
    {
        NavSpace space = Surfaces(146f, 146f);
        var cells = new SeenCells(146f);
        var guard = new RouteStraightenerTests.Sampled(cells.Contains, 146f, 146f);
        var drive = new Driver(space, (from, to) =>
        {
            if (!cells.Armed) return true;
            bool allowed = guard.Allows(from, to);
            if (!allowed) cells.Refusals++;
            return allowed;
        }, options ?? Straighten, StairStart, context);
        cells.Recorder = drive.Recorder;
        drive.Tick(StairEnd);
        Assert.NotNull(drive.Mover.Straightener!.LastStraightened);
        cells.Armed = true;

        Assert.True(drive.RunTo(StairEnd, 300));
        seen = cells;
        return drive;
    }

    // Kept waypoints of a RouteStraightener over the raw plan, without the final one.
    static IReadOnlyList<Vector2> Bends(NavSpace space, NavPath raw, Vector2 start, Func<Vector3, Vector3, bool> guard)
    {
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => raw), space, guard);
        NavPath kept = straightener.FindPath(new Vector3(start.X, 0f, start.Y), new NavGoalRegion(Vector3.Zero, 1f, _ => true),
            Tuning.CapsuleRadius, PathQueryBudget.Default);
        return kept.Waypoints.Take(kept.Waypoints.Count - 1).Select(w => w.Position).ToArray();
    }

    // Checks every moving tick before the final approach, whose command StopAtRange shortens, and returns the count
    // of heading changes. A heading change starts on a kept bend, and a tick that does not land on one travels the
    // full bound.
    static int AssertTurnsOnlyAtBends(Driver drive, IReadOnlyList<Vector2> bends)
    {
        Assert.Equal(RangeMoveStatus.InRange, drive.Steering[^1].Status);
        int finalApproach = drive.Steering.Count - 2;
        int turns = 0;
        for (int tick = 0; tick < finalApproach; tick++)
        {
            Vector2 at = Xz(drive.Bodies[tick]), next = Xz(drive.Bodies[tick + 1]);
            Vector2 direction = drive.Steering[tick].WorldDirection;
            Assert.NotEqual(Vector2.Zero, direction);
            if (tick > 0 && Turn(drive.Steering[tick - 1].WorldDirection, direction) > TurnTolerance)
            {
                Assert.True(bends.Contains(at), $"tick {tick} turned at {at}, not on a kept bend");
                turns++;
            }
            if (bends.Contains(next)) continue;
            double travel = Vector2.Distance(at, next);
            Assert.True(Math.Abs(travel - Bound) <= TravelTolerance, $"tick {tick} travelled {travel:F7} m of {Bound:F7} m");
        }
        return turns;
    }

    static bool SameBits(Vector2 a, Vector2 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
            && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y);

    static double Turn(Vector2 a, Vector2 b)
        => Math.Abs(Math.Atan2((double)a.X * b.Y - (double)a.Y * b.X, (double)a.X * b.X + (double)a.Y * b.Y));

    static ReachTarget Point(Vector2 end) => ReachTarget.Point(new(end.X, 0.75f, end.Y));

    static Vector2 Xz(in MoveState body) => new(body.Position.X, body.Position.Z);

    static Vector2 CellCentre(float origin, int x, int z) => new(origin + (x + 0.5f) * 0.25f, origin + (z + 0.5f) * 0.25f);

    /// <summary>Drives one body through Tick and NpcGroundMovement.Step. Bodies[i] is the body a tick i saw, and
    /// PlanCounts[i] the raw plans recorded after it.</summary>
    internal sealed class Driver
    {
        private readonly GroundMoveContext _context;

        public Driver(NavSpace space, Func<Vector3, Vector3, bool> guard, RouteApproachOptions options, Vector2 start,
            GroundMoveContext? context = null)
        {
            _context = context ?? Flat;
            Recorder = new RecordingPlanner(new GridPathPlanner(space));
            Mover = new MoveToRange(Recorder, space, guard, null, options);
            Body = MoveToRangeTests.Body(start.X, start.Y);
            Bodies.Add(Body);
        }

        public RecordingPlanner Recorder { get; }
        public MoveToRange Mover { get; }
        public MoveState Body { get; private set; }
        public List<MoveState> Bodies { get; } = new();
        public List<RangeSteering> Steering { get; } = new();
        public List<int> PlanCounts { get; } = new();

        public RangeSteering Tick(Vector2 end)
        {
            RangeSteering steering = Mover.Tick(Body, Tuning, Point(end), 0f, false, Dt, _context);
            Steering.Add(steering);
            PlanCounts.Add(Recorder.Plans.Count);
            if (steering.Status == RangeMoveStatus.InRange) return steering;
            Body = NpcGroundMovement.Step(Body, steering, false, Dt, Tuning, _context);
            Bodies.Add(Body);
            return steering;
        }

        /// <summary>Ticks until InRange, asserting Following on the way, and starts the record afresh.</summary>
        public bool RunTo(Vector2 end, int ticks)
        {
            if (Steering.Count > 0 && Steering[^1].Status == RangeMoveStatus.InRange)
            {
                Steering.Clear();
                PlanCounts.Clear();
                Bodies.RemoveRange(0, Bodies.Count - 1);
            }
            for (int tick = 0; tick < ticks; tick++)
            {
                RangeSteering steering = Tick(end);
                if (steering.Status == RangeMoveStatus.InRange) return true;
                Assert.Equal(RangeMoveStatus.Following, steering.Status);
            }
            return false;
        }
    }

    /// <summary>Cells crossed by the polyline of every recorded raw plan, from its query start through each
    /// waypoint.</summary>
    internal sealed class SeenCells(float origin)
    {
        private readonly HashSet<(int, int)> _cells = new();
        private int _synced;

        public RecordingPlanner? Recorder { get; set; }
        public bool Armed { get; set; }
        public int Refusals { get; set; }

        public bool Contains(int x, int z)
        {
            for (; Recorder is not null && _synced < Recorder.Plans.Count; _synced++)
            {
                var from = new Vector2(Recorder.Starts[_synced].X, Recorder.Starts[_synced].Z);
                foreach (NavWaypoint waypoint in Recorder.Plans[_synced].Waypoints)
                {
                    foreach ((double px, double pz) in RouteStraightenerTests.PointsAlong(from, waypoint.Position))
                        _cells.Add(((int)Math.Floor((px - origin) / 0.25d), (int)Math.Floor((pz - origin) / 0.25d)));
                    from = waypoint.Position;
                }
            }
            return _cells.Contains((x, z));
        }
    }
}
