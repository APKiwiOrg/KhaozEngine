using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeCarryTests;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class RouteStraightenerTests
{
    const float CellSize = 0.25f;
    static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;
    static readonly Func<int, int, bool> Everywhere = (_, _) => true;

    [Theory]
    [InlineData(-4f)]
    [InlineData(128f)]
    [InlineData(184f)]
    public void OpenFieldCollapsesToOneSegment(float origin)
    {
        NavSpace space = Surfaces(origin, origin);
        var planner = new GridPathPlanner(space);
        Vector3 start = Centre(origin, 2, 2), goal = Centre(origin, 26, 14);
        NavPath raw = planner.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);
        var straightener = new RouteStraightener(planner, space, new Sampled(Everywhere, origin, origin).Allows);

        NavPath path = straightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.True(raw.Waypoints.Count >= 13, $"raw has {raw.Waypoints.Count} waypoints");
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { raw.Waypoints[^1] }, path.Waypoints);
        Assert.Same(path, straightener.LastStraightened);
    }

    [Fact]
    public void ConvexObstacleKeepsItsCorner()
    {
        Func<int, int, bool> standable = (x, z) => x < 10 || x > 13 || z < 5 || z > 11;
        NavSpace space = Surfaces(-4f, -4f, standable);
        var planner = new GridPathPlanner(space);
        Vector3 start = Centre(-4f, 4, 8), goal = Centre(-4f, 20, 8);
        NavPath raw = planner.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);
        var guard = new Sampled(standable, -4f, -4f);
        var straightener = new RouteStraightener(planner, space, guard.Allows);

        NavPath path = straightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.InRange(path.Waypoints.Count, 2, 3);
        AssertOrderedSubsequence(raw, path);
        Assert.Equal(raw.Waypoints[^1], path.Waypoints[^1]);
        for (int i = 0; i < path.Waypoints.Count - 1; i++)
        {
            int index = IndexIn(raw, path.Waypoints[i]);
            Vector2 before = index == 0 ? new Vector2(start.X, start.Z) : raw.Waypoints[index - 1].Position;
            Vector2 at = raw.Waypoints[index].Position, after = raw.Waypoints[index + 1].Position;
            Assert.True(Direction(before, at) != Direction(at, after), $"kept waypoint {index} is not a raw bend");
            (int x, int z) = CellIndex(-4f, at);
            int distance = Math.Max(Math.Max(10 - x, x - 13), Math.Max(5 - z, z - 11));
            Assert.InRange(distance, 1, 2);
        }
        Vector3 from = start;
        foreach (NavWaypoint kept in path.Waypoints)
        {
            Vector3 to = Flat3(kept.Position);
            Assert.True(guard.Allows(from, to), $"kept segment {from} -> {to} leaves standable cells");
            from = to;
        }
    }

    [Fact]
    public void AreaMaskBlocksAShortcut()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        static bool Masked(double x, double z) => x >= 0d && x < 0.5d && !(z > 0.5d);
        using var bake = PhysicsNavBake.Capture(context, new(-1.5f, -1.5f, 1.5f, 2f, CellSize, 4f, 5f, 0.8f, 256, 1024),
            feet => Masked(feet.X, feet.Z) ? 1u : 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, new NavAreaFilter(0u, 1u));
        var straightener = new RouteStraightener(nav.Planner, nav.Space, nav.AllowsSegment);
        var start = new Vector3(-0.625f, 0f, -0.625f);
        var goal = new Vector3(1.125f, 0f, -0.625f);
        NavPath raw = nav.Planner.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        NavPath path = straightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count < raw.Waypoints.Count, "the detour should still straighten");
        Vector2 from = new(start.X, start.Z);
        foreach (NavWaypoint kept in path.Waypoints)
        {
            Assert.DoesNotContain(PointsAlong(from, kept.Position), p => Masked(p.X, p.Z));
            from = kept.Position;
        }
    }

    [Fact]
    public void SideLineRefusalBesideAWall()
    {
        NavSpace space = Surfaces(-4f, -4f);
        var planner = new GridPathPlanner(space);
        Vector3 start = Centre(-4f, 2, 2), goal = Centre(-4f, 10, 6);
        Vector2 a = new(start.X, start.Z), b = new(goal.X, goal.Z);
        var centre = PointsAlong(a, b).Select(p => CellIndex(-4f, p)).ToHashSet();
        (Vector2 sideA, Vector2 sideB) = MinusSide(a, b);
        (int X, int Z) wall = PointsAlong(sideA, sideB).Select(p => CellIndex(-4f, p)).First(c => !centre.Contains(c));
        Func<int, int, bool> standable = (x, z) => (x, z) != wall;
        var guard = new Sampled(standable, -4f, -4f);
        var straightener = new RouteStraightener(planner, space, guard.Allows);
        var openStraightener = new RouteStraightener(planner, space, new Sampled(Everywhere, -4f, -4f).Allows);
        NavPath raw = planner.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        NavPath path = straightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        AssertCentreLinesClear(start, raw.Waypoints.Select(w => CellIndex(-4f, w.Position)), standable);
        Assert.Single(openStraightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default).Waypoints);
        Assert.True(path.Waypoints.Count > 1, $"kept {path.Waypoints.Count}");
    }

    [Fact]
    public void SideLinesJudgeMirroredWallsAlike()
    {
        // Review Focus 1: a side line on the cell boundary would floor into one neighbour row and not the other.
        var row = Enumerable.Range(3, 10).Select(x => (X: x, Z: 8)).ToArray();
        int above = KeptCount(Centre(-4f, 2, 8), row, (_, z) => z != 9);
        int below = KeptCount(Centre(-4f, 2, 8), row, (_, z) => z != 7);
        Assert.Equal(1, above);
        Assert.Equal(1, below);

        NavSpace open = Surfaces(-4f, -4f);
        Vector3 start = Centre(-4f, 2, 2), goal = Centre(-4f, 12, 7);
        NavPath raw = new GridPathPlanner(open).FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);
        var cells = raw.Waypoints.Select(w => CellIndex(-4f, w.Position)).ToArray();
        Vector2 a = new(start.X, start.Z), b = new(goal.X, goal.Z);
        var centre = PointsAlong(a, b).Select(p => CellIndex(-4f, p)).ToHashSet();
        (Vector2 sideA, Vector2 sideB) = MinusSide(a, b);
        (int X, int Z) wall = PointsAlong(sideA, sideB).Select(p => CellIndex(-4f, p)).First(c => !centre.Contains(c));
        var mirroredCells = cells.Select(c => (c.X, 31 - c.Z)).ToArray();
        AssertCentreLinesClear(start, cells, (x, z) => (x, z) != wall);
        AssertCentreLinesClear(Centre(-4f, 2, 29), mirroredCells, (x, z) => (x, 31 - z) != wall);
        int oblique = KeptCount(start, cells, (x, z) => (x, z) != wall);
        int mirrored = KeptCount(Centre(-4f, 2, 29), mirroredCells, (x, z) => (x, 31 - z) != wall);
        Assert.True(oblique > 1, $"kept {oblique}");
        Assert.Equal(oblique, mirrored);
    }

    [Fact]
    public void MandatoryWaypointsAreKept()
    {
        NavGrid Layer(float height) => NavGrid.FromSurfaces(16, 8, CellSize, -1f, -1f,
            (_, _) => new NavSurfaceSample(true, height, float.PositiveInfinity), stepHeight: 0.4f, agentHeight: 1.5f);
        var space = new NavSpace(new[] { Layer(0f), Layer(2f) });
        NavWaypoint[] route =
        {
            new(new(0f, 0f), 0), new(new(0.25f, 0f), 0), new(new(0.5f, 0f), 0),
            new(new(0.75f, 0f), 1), new(new(1f, 0f), 1), new(new(1.25f, 0f), 1),
            new(new(2f, 0f), 1) { Kind = NavWaypointKind.Hop }, new(new(2.25f, 0f), 1), new(new(2.5f, 0f), 1),
        };
        var straightener = new RouteStraightener(
            new ScriptPlanner((_, _) => new NavPath(NavPathStatus.Complete, route)), space, Open);

        NavPath path = straightener.FindPath(new Vector3(-0.25f, 0f, 0f), GoalAt(Vector3.Zero),
            Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.Equal(new[] { route[2], route[3], route[5], route[6], route[8] }, path.Waypoints);
    }

    [Fact]
    public void PartialStatusIsKept()
    {
        NavPath raw = CellRoute(NavPathStatus.Partial, Enumerable.Range(3, 6).Select(x => (x, 8)).ToArray());
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => raw), Surfaces(-4f, -4f), Open);

        NavPath path = straightener.FindPath(Centre(-4f, 2, 8), GoalAt(Vector3.Zero), Tuning.CapsuleRadius,
            PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { raw.Waypoints[^1] }, path.Waypoints);
    }

    [Fact]
    public void UnreachablePassesThroughByReference()
    {
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => NavPath.Unreachable), Surfaces(-4f, -4f), Open);

        NavPath path = straightener.FindPath(Centre(-4f, 2, 8), GoalAt(Vector3.Zero), Tuning.CapsuleRadius,
            PathQueryBudget.Default);

        Assert.Same(NavPath.Unreachable, path);
        Assert.Null(straightener.LastStraightened);
    }

    [Fact]
    public void PointQueriesAreStraightenedToo()
    {
        // A legacy point query on GridPathPlanner is already string-pulled, so the raw cell route comes from a
        // profile planner, whose point queries keep every cell edge.
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, new(-0.5f, -0.5f, 6.5f, 3.5f, CellSize, 4f, 5f, 0.8f, 512, 2048),
            _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        var straightener = new RouteStraightener(nav.Planner, nav.Space, nav.AllowsSegment);
        Vector3 start = Centre(-0.5f, 2, 2), goal = Centre(-0.5f, 26, 14);
        NavPath raw = nav.Planner.FindPath(start, goal, Tuning.CapsuleRadius, PathQueryBudget.Default);

        NavPath path = straightener.FindPath(start, goal, Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.True(raw.Waypoints.Count >= 13, $"raw has {raw.Waypoints.Count} waypoints");
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(new[] { raw.Waypoints[^1] }, path.Waypoints);
    }

    [Fact]
    public void FallBackOnceReturnsTheRawRouteForOnePlan()
    {
        NavPath raw = CellRoute(NavPathStatus.Complete, Enumerable.Range(3, 6).Select(x => (x, 8)).ToArray());
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => raw), Surfaces(-4f, -4f), Open);
        NavPath Plan() => straightener.FindPath(Centre(-4f, 2, 8), GoalAt(Vector3.Zero), Tuning.CapsuleRadius,
            PathQueryBudget.Default);
        Assert.NotNull(Plan());
        Assert.NotNull(straightener.LastStraightened);

        straightener.FallBackOnce();
        NavPath first = Plan();
        Assert.Same(raw, first);
        Assert.Null(straightener.LastStraightened);

        NavPath second = Plan();
        Assert.Single(second.Waypoints);
        Assert.Same(second, straightener.LastStraightened);
    }

    [Fact]
    public void UnchangedRouteIsReturnedByReference()
    {
        // A straight diagonal run with walls on both sides: each shortcut's side lines enter a wall.
        NavPath raw = CellRoute(NavPathStatus.Complete, (3, 3), (4, 4), (5, 5));
        var guard = new Sampled((x, z) => Math.Abs(x - z) != 1, -4f, -4f);
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => raw), Surfaces(-4f, -4f), guard.Allows);

        NavPath path = straightener.FindPath(Centre(-4f, 2, 2), GoalAt(Vector3.Zero), Tuning.CapsuleRadius,
            PathQueryBudget.Default);

        Assert.Same(raw, path);
        Assert.Null(straightener.LastStraightened);
    }

    [Fact]
    public void ScanCostIsBounded()
    {
        NavSpace space = Surfaces(146f, 146f);
        var planner = new GridPathPlanner(space);
        Vector3 start = Centre(146f, 2, 2), goal = Centre(146f, 26, 14);
        NavPath raw = planner.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);
        var guard = new Sampled(Everywhere, 146f, 146f);
        var straightener = new RouteStraightener(planner, space, guard.Allows);

        NavPath path = straightener.FindPath(start, GoalAt(goal), Tuning.CapsuleRadius, PathQueryBudget.Default);

        Assert.True(guard.Calls > 0);
        Assert.True(guard.Calls <= 3 * (raw.Waypoints.Count + path.Waypoints.Count),
            $"{guard.Calls} guard calls for {raw.Waypoints.Count} raw and {path.Waypoints.Count} kept waypoints");
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        var planner = new ScriptPlanner((_, _) => NavPath.Unreachable);
        NavSpace space = Surfaces(-4f, -4f);
        Assert.Equal("inner", Assert.Throws<ArgumentNullException>(() => new RouteStraightener(null!, space, Open)).ParamName);
        Assert.Equal("space", Assert.Throws<ArgumentNullException>(() => new RouteStraightener(planner, null!, Open)).ParamName);
        Assert.Equal("allowsSegment",
            Assert.Throws<ArgumentNullException>(() => new RouteStraightener(planner, space, null!)).ParamName);
    }

    static int KeptCount(Vector3 start, (int X, int Z)[] cells, Func<int, int, bool> standable)
    {
        var straightener = new RouteStraightener(new ScriptPlanner((_, _) => CellRoute(NavPathStatus.Complete, cells)),
            Surfaces(-4f, -4f), new Sampled(standable, -4f, -4f).Allows);
        return straightener.FindPath(start, GoalAt(Vector3.Zero), Tuning.CapsuleRadius, PathQueryBudget.Default)
            .Waypoints.Count;
    }

    // Every centre line from the start passes, so the first scan can only stop on a side line, and a route that
    // keeps more than its final waypoint proves a side line refused.
    static void AssertCentreLinesClear(Vector3 start, IEnumerable<(int X, int Z)> cells, Func<int, int, bool> standable)
    {
        var guard = new Sampled(standable, -4f, -4f);
        foreach ((int x, int z) in cells)
            Assert.True(guard.Allows(start, Centre(-4f, x, z)), $"the centre line to ({x}, {z}) is refused");
    }

    static Vector3 Centre(float origin, int x, int z)
        => new(origin + (x + 0.5f) * CellSize, 0f, origin + (z + 0.5f) * CellSize);

    static Vector3 Flat3(Vector2 point) => new(point.X, 0f, point.Y);

    static NavPath CellRoute(NavPathStatus status, params (int X, int Z)[] cells)
        => new(status, cells.Select(c => new NavWaypoint(new Vector2(Centre(-4f, c.X, c.Z).X, Centre(-4f, c.X, c.Z).Z), 0))
            .ToArray());

    static NavGoalRegion GoalAt(Vector3 feet)
        => new(feet, 0.01f, f => Math.Abs(f.X - feet.X) < 0.001f && Math.Abs(f.Z - feet.Z) < 0.001f);

    static (int X, int Z) CellIndex(float origin, Vector2 point) => CellIndex(origin, (point.X, point.Y));

    static (int X, int Z) CellIndex(float origin, (double X, double Z) point)
        => ((int)Math.Floor((point.X - origin) / CellSize), (int)Math.Floor((point.Z - origin) / CellSize));

    static (Vector2 A, Vector2 B) MinusSide(Vector2 a, Vector2 b)
    {
        double dx = (double)b.X - a.X, dz = (double)b.Y - a.Y, offset = RouteStraightener.SideOffsetCells * CellSize /
            Math.Sqrt(dx * dx + dz * dz);
        return (new((float)(a.X + dz * offset), (float)(a.Y - dx * offset)),
            new((float)(b.X + dz * offset), (float)(b.Y - dx * offset)));
    }

    internal static IEnumerable<(double X, double Z)> PointsAlong(Vector2 a, Vector2 b)
    {
        double dx = (double)b.X - a.X, dz = (double)b.Y - a.Y;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.001));
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            yield return (a.X + t * dx, a.Y + t * dz);
        }
    }

    static int IndexIn(NavPath raw, NavWaypoint waypoint)
    {
        for (int i = 0; i < raw.Waypoints.Count; i++)
            if (raw.Waypoints[i] == waypoint) return i;
        throw new InvalidOperationException($"{waypoint} is not a raw waypoint");
    }

    static void AssertOrderedSubsequence(NavPath raw, NavPath kept)
    {
        int next = 0;
        foreach (NavWaypoint waypoint in kept.Waypoints)
        {
            while (next < raw.Waypoints.Count && raw.Waypoints[next] != waypoint) next++;
            Assert.True(next < raw.Waypoints.Count, $"{waypoint} is not in raw order");
            next++;
        }
    }

    static (int, int) Direction(Vector2 from, Vector2 to)
        => (Math.Sign(MathF.Round((to.X - from.X) / CellSize)), Math.Sign(MathF.Round((to.Y - from.Y) / CellSize)));

    /// <summary>Refuses a segment when any point at 1 mm spacing lies in an unstandable or off-grid cell.</summary>
    internal sealed class Sampled(Func<int, int, bool> standable, float originX, float originZ)
    {
        public int Calls { get; private set; }

        public bool Allows(Vector3 from, Vector3 to)
        {
            Calls++;
            foreach ((double px, double pz) in PointsAlong(new(from.X, from.Z), new(to.X, to.Z)))
            {
                int x = (int)Math.Floor((px - originX) / CellSize);
                int z = (int)Math.Floor((pz - originZ) / CellSize);
                if (x < 0 || z < 0 || x >= 32 || z >= 32 || !standable(x, z)) return false;
            }
            return true;
        }
    }
}
