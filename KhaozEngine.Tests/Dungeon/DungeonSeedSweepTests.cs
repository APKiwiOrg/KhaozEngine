using System.Diagnostics;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Dungeon
{
    // Task 14: the thousand-seed regression net. Tasks 3-8 already guarantee every invariant checked here, so this
    // sweep is expected to pass on first run; it exists to catch a future regression across a wide seed range, not
    // to prove anything new about the generator today.
    public class DungeonSeedSweepTests
    {
        readonly ITestOutputHelper _out;
        public DungeonSeedSweepTests(ITestOutputHelper output) => _out = output;

        // Measured locally at ~1000 seeds well under the 60s guard (see the runtime report written by the Fact
        // below), so the full 0..999 range stays in. If a future slowdown pushes this sweep past ~60s, drop to
        // 250 and note the measured runtime here.
        const int SeedCount = 1000;

        static DungeonConfig Config() => new()
        {
            RoomCountTarget = 14,
            MaxFloors = 3,
            LockCount = 2,
            LoopEdgeBudget = 2,
        };

        // Same shape as Config() but with wide corridors, elongated halls, and a larger plot to place them in.
        // Exercises the widened growth + loop carve and the hall room type across the full seed range.
        static DungeonConfig WideConfig() => new()
        {
            RoomCountTarget = 14,
            MaxFloors = 3,
            LockCount = 2,
            LoopEdgeBudget = 3,
            PlotWidthTiles = 80,
            PlotDepthTiles = 80,
            CorridorMinWidth = 1,
            CorridorMaxWidth = 4,
            HallChancePercent = 25,
            HallMinLengthTiles = 10,
            HallMaxLengthTiles = 16,
        };

        [Fact]
        public void SweepThousandSeeds_AllSolvable_AllInvariants()
        {
            DungeonConfig config = Config();
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (ulong seed = 0; seed < SeedCount; seed++)
            {
                // Generate throws InvalidOperationException on an unsolvable layout (DungeonSolver.Verify inside
                // DungeonGenerator.Generate), so a bad seed fails loudly here before we even reach the asserts below.
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);

                DungeonSolveReport report = DungeonSolver.Verify(layout);
                Assert.True(report.IsSolvable, $"seed {seed} produced an unsolvable layout: {string.Join(" ", report.Errors)}");
                Assert.Empty(report.Errors);

                AssertWallPassHolds(layout, seed);
                AssertPlacementsWithinPlotBounds(layout, seed);
                AssertStairPairsConsistent(layout, seed);
            }

            stopwatch.Stop();
            _out.WriteLine($"Swept {SeedCount} seeds (0..{SeedCount - 1}) in {stopwatch.ElapsedMilliseconds} ms.");

            Assert.True(
                stopwatch.Elapsed.TotalSeconds < 60,
                $"sweep of {SeedCount} seeds took {stopwatch.Elapsed.TotalSeconds:F1}s, exceeding the 60s runtime guard.");
        }

        [Fact]
        public void SweepThousandSeeds_WideCorridorsAndHalls_AllSolvable_AllInvariants()
        {
            DungeonConfig config = WideConfig();
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool sawWideCorridor = false;
            bool sawHall = false;

            for (ulong seed = 0; seed < SeedCount; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);

                DungeonSolveReport report = DungeonSolver.Verify(layout);
                Assert.True(report.IsSolvable, $"seed {seed} produced an unsolvable layout: {string.Join(" ", report.Errors)}");
                Assert.Empty(report.Errors);

                AssertWallPassHolds(layout, seed);
                AssertPlacementsWithinPlotBounds(layout, seed);
                AssertStairPairsConsistent(layout, seed);
                AssertCorridorBandsRectangular(layout, seed);

                foreach (DungeonEdge edge in layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Corridor))
                {
                    if (edge.Doors.Count > 2)
                    {
                        sawWideCorridor = true;
                    }
                }

                if (layout.Rooms.Any(r => r.RoomType == DungeonRoomType.Hall))
                {
                    sawHall = true;
                }
            }

            stopwatch.Stop();
            _out.WriteLine($"Swept {SeedCount} wide+hall seeds (0..{SeedCount - 1}) in {stopwatch.ElapsedMilliseconds} ms.");

            // The sweep is only meaningful if the new code paths actually fire somewhere in the range.
            Assert.True(sawWideCorridor, "no wide corridor was produced across the wide-config seed range");
            Assert.True(sawHall, "no hall room was produced across the wide-config seed range");

            Assert.True(
                stopwatch.Elapsed.TotalSeconds < 60,
                $"wide+hall sweep of {SeedCount} seeds took {stopwatch.Elapsed.TotalSeconds:F1}s, exceeding the 60s runtime guard.");
        }

        // Every corridor edge is a straight rectangular tube: its door band is 2 * w cells (w per end) and its
        // path is a multiple of that per-end width, spanning exactly w cells on one axis.
        static void AssertCorridorBandsRectangular(DungeonLayout layout, ulong seed)
        {
            foreach (DungeonEdge edge in layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Corridor))
            {
                Assert.True(edge.Doors.Count % 2 == 0, $"seed {seed}: corridor {edge.RoomA}->{edge.RoomB} has an odd door count {edge.Doors.Count}.");
                int w = edge.Doors.Count / 2;
                Assert.True(w >= 1, $"seed {seed}: corridor {edge.RoomA}->{edge.RoomB} has width {w}.");
                Assert.True(edge.Path.Count % w == 0, $"seed {seed}: corridor {edge.RoomA}->{edge.RoomB} path {edge.Path.Count} is not a multiple of width {w}.");

                int perpAxisSpanX = edge.Path.Select(t => t.X).Distinct().Count();
                int perpAxisSpanZ = edge.Path.Select(t => t.Z).Distinct().Count();
                Assert.True(perpAxisSpanX == w || perpAxisSpanZ == w,
                    $"seed {seed}: corridor {edge.RoomA}->{edge.RoomB} band does not span exactly width {w} on one axis (X={perpAxisSpanX}, Z={perpAxisSpanZ}).");
            }
        }

        // No walkable cell may be 8-adjacent (same floor) to an Empty cell after the wall pass.
        static void AssertWallPassHolds(DungeonLayout layout, ulong seed)
        {
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    for (int z = 0; z < layout.Depth; z++)
                    {
                        if (!DungeonLayout.IsWalkable(layout.GetCell(x, z, f)))
                        {
                            continue;
                        }

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                Assert.True(
                                    layout.GetCell(x + dx, z + dz, f) != DungeonCellKind.Empty,
                                    $"seed {seed}: walkable cell ({x},{z},{f}) is 8-adjacent to Empty at ({x + dx},{z + dz},{f}).");
                            }
                        }
                    }
                }
            }
        }

        // Every room rect and every edge path/door tile must sit inside the raster the layout was allocated with.
        static void AssertPlacementsWithinPlotBounds(DungeonLayout layout, ulong seed)
        {
            foreach (DungeonRoom room in layout.Rooms)
            {
                Assert.True(room.Floor >= 0 && room.Floor < layout.Floors, $"seed {seed}: room {room.Id} floor {room.Floor} is out of bounds.");
                Assert.True(room.X >= 0 && room.X + room.Width <= layout.Width, $"seed {seed}: room {room.Id} X span [{room.X},{room.X + room.Width}) is out of bounds.");
                Assert.True(room.Z >= 0 && room.Z + room.Depth <= layout.Depth, $"seed {seed}: room {room.Id} Z span [{room.Z},{room.Z + room.Depth}) is out of bounds.");
            }

            foreach (DungeonEdge edge in layout.Edges)
            {
                foreach (DungeonTile tile in edge.Path)
                {
                    AssertTileWithinBounds(layout, tile, edge, "path", seed);
                }

                foreach (DungeonTile tile in edge.Doors)
                {
                    AssertTileWithinBounds(layout, tile, edge, "door", seed);
                }
            }
        }

        static void AssertTileWithinBounds(DungeonLayout layout, DungeonTile tile, DungeonEdge edge, string role, ulong seed)
        {
            bool inBounds = tile.X >= 0 && tile.X < layout.Width
                && tile.Z >= 0 && tile.Z < layout.Depth
                && tile.Floor >= 0 && tile.Floor < layout.Floors;

            Assert.True(
                inBounds,
                $"seed {seed}: edge {edge.RoomA}->{edge.RoomB} {role} tile ({tile.X},{tile.Z},{tile.Floor}) is out of plot bounds.");
        }

        // Every stair edge is the [StairLower, StairUpper, StairTop] run the geometry model requires, with the
        // StairVoid headroom cutout directly above StairLower on the floor above.
        static void AssertStairPairsConsistent(DungeonLayout layout, ulong seed)
        {
            foreach (DungeonEdge edge in layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair))
            {
                Assert.True(edge.Path.Count == 3, $"seed {seed}: stair edge {edge.RoomA}->{edge.RoomB} has {edge.Path.Count} path cells, expected 3.");

                DungeonTile lower = edge.Path[0];
                DungeonTile upper = edge.Path[1];
                DungeonTile top = edge.Path[2];

                Assert.True(lower.Floor == upper.Floor, $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} lower/upper floor mismatch.");
                Assert.True(upper.Floor + 1 == top.Floor, $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} upper/top floor mismatch.");
                Assert.True(upper.X == top.X && upper.Z == top.Z, $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} upper/top XZ mismatch.");

                Assert.Equal(DungeonCellKind.StairLower, layout.GetCell(lower.X, lower.Z, lower.Floor));
                Assert.Equal(DungeonCellKind.StairUpper, layout.GetCell(upper.X, upper.Z, upper.Floor));
                Assert.Equal(DungeonCellKind.StairTop, layout.GetCell(top.X, top.Z, top.Floor));

                // The cell directly above StairLower (its head-room cutout on the upper floor) is StairVoid.
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(lower.X, lower.Z, lower.Floor + 1));
            }
        }
    }
}
