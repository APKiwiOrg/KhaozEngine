using System;
using System.Diagnostics;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Dungeon
{
    // Task 14: the thousand-seed regression net. Tasks 3-8 already guarantee every invariant checked here, so this
    // sweep is expected to pass on first run: it exists to catch a future regression across a wide seed range, not
    // to prove anything new about the generator today.
    //
    // WHAT BOUNDS THE SWEEP (#217, #507, #651). The bound used to be a stopwatch, 60s on the wide sweep and 180s on
    // the narrow one. It tripped four times on hosted runners, at 1.1x, 1.3x, 1.8x and 6.9x its budget, and caught a
    // generator regression exactly zero times: a wall-clock number on a shared VM measures the VM, and every trip
    // reds a blocking leg and costs a full re-run. Raising the constant only moves the same coin flip further out.
    // The bounds below move when the GENERATOR moves and not otherwise:
    //   - DETERMINISM. A sampled seed is generated twice and both layouts must fold to the same LayoutHash, so an
    //     ambient-state dependence (a clock read, an unordered walk, a static cache) fails here rather than
    //     desyncing a client from the server later. Nothing else in the suite asserts this across a seed range.
    //   - PER-SEED WORK. The geometry a seed carves stays inside a budget. A runaway carve or route loop is the
    //     algorithmic blowup the stopwatch was really watching for (the only kind of slowdown that costs minutes
    //     rather than seconds), and it moves these counts. This fails on the offending seed BY NAME, where the
    //     stopwatch failed on whichever seed the runner happened to be busy during.
    // The elapsed time is still measured and still written to the test output, total plus per-seed median and the
    // slowest seed. Read it when you want the number. Nothing asserts on it.
    public class DungeonSeedSweepTests
    {
        readonly ITestOutputHelper _out;
        public DungeonSeedSweepTests(ITestOutputHelper output) => _out = output;

        // The full 0..999 range stays in: the sweep costs a fraction of a second per hundred seeds, and the range is
        // the point (a rare seed is exactly what this net is for).
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

        // Per-seed geometry, counted while the invariants are walked (so it costs nothing extra) and bounded below.
        readonly record struct SeedWork(int WalkableCells, int PathTiles, int Rooms, int Edges)
        {
            public SeedWork Max(SeedWork other) => new(
                Math.Max(WalkableCells, other.WalkableCells),
                Math.Max(PathTiles, other.PathTiles),
                Math.Max(Rooms, other.Rooms),
                Math.Max(Edges, other.Edges));

            public override string ToString() =>
                $"{WalkableCells} walkable cells, {PathTiles} path tiles, {Rooms} rooms, {Edges} edges";
        }

        // Budgets: roughly 2x the peak each config actually produces across the whole 0..999 range. Measured, not
        // guessed (2026-08-19: narrow peaked at 637 walkable cells / 54 path tiles / 14 rooms / 15 edges, wide at
        // 1009 / 117 / 14 / 16), and the sweep prints the peak it saw on every run, so drift shows in the log
        // without anyone editing a constant. Loose enough that ordinary retuning of room counts or corridor widths
        // never touches them, tight enough that a carve or route loop which runs away goes straight through.
        static readonly SeedWork NarrowBudget = new(WalkableCells: 1300, PathTiles: 120, Rooms: 28, Edges: 40);
        static readonly SeedWork WideBudget = new(WalkableCells: 2100, PathTiles: 250, Rooms: 28, Edges: 40);

        // Determinism is a property of the generator, not of a particular seed, so it is checked on a spread SAMPLE
        // (every 20th seed, 50 of them) rather than on all 1000. An ambient-state dependence shows up on nearly
        // every seed, so the sample catches it, and the sweep does not pay a second full pass of generation to find
        // that out.
        const int DeterminismSampleStride = 20;

        [Fact]
        public void SweepThousandSeeds_AllSolvable_AllInvariants()
        {
            DungeonConfig config = Config();
            var perSeedTicks = new long[SeedCount];
            SeedWork peak = default;
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (ulong seed = 0; seed < SeedCount; seed++)
            {
                long started = stopwatch.ElapsedTicks;

                // Generate throws InvalidOperationException on an unsolvable layout (DungeonSolver.Verify inside
                // DungeonGenerator.Generate), so a bad seed fails loudly here before we even reach the asserts below.
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);

                peak = peak.Max(AssertSeedInvariants(config, layout, seed, NarrowBudget));
                perSeedTicks[seed] = stopwatch.ElapsedTicks - started;
            }

            stopwatch.Stop();
            ReportSweep("", stopwatch, perSeedTicks, peak);
        }

        [Fact]
        public void SweepThousandSeeds_WideCorridorsAndHalls_AllSolvable_AllInvariants()
        {
            DungeonConfig config = WideConfig();
            var perSeedTicks = new long[SeedCount];
            SeedWork peak = default;
            bool sawWideCorridor = false;
            bool sawHall = false;
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (ulong seed = 0; seed < SeedCount; seed++)
            {
                long started = stopwatch.ElapsedTicks;
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);

                peak = peak.Max(AssertSeedInvariants(config, layout, seed, WideBudget));
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

                perSeedTicks[seed] = stopwatch.ElapsedTicks - started;
            }

            stopwatch.Stop();
            ReportSweep("wide+hall ", stopwatch, perSeedTicks, peak);

            // The sweep is only meaningful if the new code paths actually fire somewhere in the range.
            Assert.True(sawWideCorridor, "no wide corridor was produced across the wide-config seed range");
            Assert.True(sawHall, "no hall room was produced across the wide-config seed range");
        }

        // Everything one seed owes: solvable, every layout invariant, a carve that stays inside the per-seed budget,
        // and (on a sampled seed) a second generation that lands on the same layout. Returns the geometry it counted
        // so the caller can carry the peak.
        static SeedWork AssertSeedInvariants(DungeonConfig config, DungeonLayout layout, ulong seed, SeedWork budget)
        {
            DungeonSolveReport report = DungeonSolver.Verify(layout);
            Assert.True(report.IsSolvable, $"seed {seed} produced an unsolvable layout: {string.Join(" ", report.Errors)}");
            Assert.Empty(report.Errors);

            int walkable = AssertWallPassHolds(layout, seed);
            AssertPlacementsWithinPlotBounds(layout, seed);
            AssertStairPairsConsistent(layout, seed);
            if (seed % DeterminismSampleStride == 0) AssertRegeneratesIdentically(config, layout, seed);

            var work = new SeedWork(walkable, layout.Edges.Sum(e => e.Path.Count), layout.Rooms.Count, layout.Edges.Count);
            AssertWithinBudget(work, budget, seed);

            // The generator can decline to place a room it cannot fit, never invent one: a placed count above the
            // requested target means the room loop itself ran away, which is the cheapest possible read on it.
            Assert.True(layout.Rooms.Count <= config.RoomCountTarget,
                $"seed {seed}: placed {layout.Rooms.Count} rooms against a target of {config.RoomCountTarget}.");
            return work;
        }

        static void AssertWithinBudget(SeedWork work, SeedWork budget, ulong seed)
        {
            if (work.WalkableCells > budget.WalkableCells)
                Assert.Fail($"seed {seed}: carved {work.WalkableCells} walkable cells, over the {budget.WalkableCells} per-seed budget.");
            if (work.PathTiles > budget.PathTiles)
                Assert.Fail($"seed {seed}: routed {work.PathTiles} edge path tiles, over the {budget.PathTiles} per-seed budget.");
            if (work.Rooms > budget.Rooms)
                Assert.Fail($"seed {seed}: placed {work.Rooms} rooms, over the {budget.Rooms} per-seed budget.");
            if (work.Edges > budget.Edges)
                Assert.Fail($"seed {seed}: produced {work.Edges} edges, over the {budget.Edges} per-seed budget.");
        }

        // Generation is a pure function of (config, seed): a second run in the same process must fold to the same
        // LayoutHash, which covers the raster, rooms, edges, keys and markers with float BITS rather than
        // GetHashCode, so it is stable across platforms and process runs (DungeonLayout.LayoutHash).
        static void AssertRegeneratesIdentically(DungeonConfig config, DungeonLayout first, ulong seed)
        {
            ulong once = first.LayoutHash();
            ulong twice = DungeonGenerator.Generate(config, seed).LayoutHash();
            if (once != twice)
                Assert.Fail($"seed {seed}: regenerating the same config produced a different layout ({once:x16} then {twice:x16}).");
        }

        void ReportSweep(string label, Stopwatch stopwatch, long[] perSeedTicks, SeedWork peak)
        {
            var sorted = (long[])perSeedTicks.Clone();
            Array.Sort(sorted);
            double toMs = 1000.0 / Stopwatch.Frequency;
            long worst = sorted[^1];
            int worstSeed = Array.IndexOf(perSeedTicks, worst);

            _out.WriteLine($"Swept {SeedCount} {label}seeds (0..{SeedCount - 1}) in {stopwatch.ElapsedMilliseconds} ms.");
            // Diagnostic only. No assertion reads these numbers, deliberately: see the note on the class.
            _out.WriteLine($"Per-seed cost: median {sorted[SeedCount / 2] * toMs:F3} ms, slowest {worst * toMs:F3} ms (seed {worstSeed}).");
            _out.WriteLine($"Peak per-seed work: {peak}.");
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

        // No walkable cell may be 8-adjacent (same floor) to an Empty cell after the wall pass. Returns the count of
        // walkable cells it visited, which is the per-seed work budget's carve measure (free here, since this is the
        // one pass that already visits every cell).
        static int AssertWallPassHolds(DungeonLayout layout, ulong seed)
        {
            int walkable = 0;

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

                        walkable++;

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

            return walkable;
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

        // Every stair edge is the [StairLower, StairMid, StairUpper, StairTop] run the geometry model requires:
        // three colinear treads on the lower floor, then the landing one cell PAST the top tread on the floor
        // above, with a StairVoid open-shaft cutout directly above every tread.
        static void AssertStairPairsConsistent(DungeonLayout layout, ulong seed)
        {
            foreach (DungeonEdge edge in layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair))
            {
                Assert.True(edge.Path.Count == 4, $"seed {seed}: stair edge {edge.RoomA}->{edge.RoomB} has {edge.Path.Count} path cells, expected 4.");

                DungeonTile lower = edge.Path[0];
                DungeonTile mid = edge.Path[1];
                DungeonTile upper = edge.Path[2];
                DungeonTile top = edge.Path[3];

                Assert.True(lower.Floor == mid.Floor && lower.Floor == upper.Floor,
                    $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} treads not all on the lower floor.");
                int dx = upper.X - mid.X, dz = upper.Z - mid.Z;
                Assert.True((mid.X - lower.X, mid.Z - lower.Z) == (dx, dz) && System.Math.Abs(dx) + System.Math.Abs(dz) == 1,
                    $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} treads not a straight unit run.");
                Assert.True(upper.Floor + 1 == top.Floor, $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} upper/top floor mismatch.");
                Assert.True(top.X == upper.X + dx && top.Z == upper.Z + dz,
                    $"seed {seed}: stair {edge.RoomA}->{edge.RoomB} landing not one cell past the top tread.");

                Assert.Equal(DungeonCellKind.StairLower, layout.GetCell(lower.X, lower.Z, lower.Floor));
                Assert.Equal(DungeonCellKind.StairMid, layout.GetCell(mid.X, mid.Z, mid.Floor));
                Assert.Equal(DungeonCellKind.StairUpper, layout.GetCell(upper.X, upper.Z, upper.Floor));
                Assert.Equal(DungeonCellKind.StairTop, layout.GetCell(top.X, top.Z, top.Floor));

                // The open shaft: StairVoid directly above every tread on the floor above.
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(lower.X, lower.Z, lower.Floor + 1));
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(mid.X, mid.Z, mid.Floor + 1));
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(upper.X, upper.Z, upper.Floor + 1));
            }
        }
    }
}
