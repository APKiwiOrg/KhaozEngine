using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // Corridor-width variety (RoomGrower + LoopPlanner). A corridor edge's Doors are the DoorFrame cells at each
    // end and its Path is the between-corridor band; a 1-wide corridor has exactly two doors, so a wider one has
    // more. These tests exercise growth corridors (LoopEdgeBudget = 0) unless they say otherwise.
    public class DungeonCorridorWidthTests
    {
        static DungeonConfig WideConfig() => new()
        {
            RoomCountTarget = 14,
            RoomMinTiles = 4,
            RoomMaxTiles = 8,
            MaxFloors = 1,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
            PlotWidthTiles = 80,
            PlotDepthTiles = 80,
            CorridorMinWidth = 3,
            CorridorMaxWidth = 3,
        };

        static int CorridorWidth(DungeonEdge edge)
        {
            // The door band is 2 * w cells (w at each end), so the per-end width is half the door count.
            return edge.Doors.Count / 2;
        }

        [Fact]
        public void WideConfig_ProducesCorridorsWiderThanOne()
        {
            DungeonLayout layout = DungeonGenerator.Generate(WideConfig(), 5UL);

            List<DungeonEdge> corridors = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Corridor).ToList();
            Assert.NotEmpty(corridors);
            Assert.Contains(corridors, e => CorridorWidth(e) >= 2);
        }

        [Fact]
        public void WideCorridor_PathIsRectangularBand_AllCorridorCells()
        {
            DungeonLayout layout = DungeonGenerator.Generate(WideConfig(), 5UL);
            DungeonEdge wide = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Corridor && CorridorWidth(e) >= 2);
            int w = CorridorWidth(wide);

            // Every path cell is a Corridor cell on one floor, and the band is a straight tube: it spans exactly w
            // distinct coordinates on one axis (the perpendicular) and every path cell shares that structure, so
            // path count is a multiple of w.
            Assert.All(wide.Path, t => Assert.Equal(DungeonCellKind.Corridor, layout.GetCell(t.X, t.Z, t.Floor)));
            Assert.Single(wide.Path.Select(t => t.Floor).Distinct());
            Assert.Equal(0, wide.Path.Count % w);

            bool constantZ = wide.Path.Select(t => t.Z).Distinct().Count() == w; // vertical march: band spans Z
            bool constantX = wide.Path.Select(t => t.X).Distinct().Count() == w; // horizontal march: band spans X
            Assert.True(constantX ^ constantZ, "a straight corridor band spans exactly w cells on one axis");
        }

        [Fact]
        public void WideCorridor_DoorBand_SitsOnBothRoomEdges()
        {
            DungeonLayout layout = DungeonGenerator.Generate(WideConfig(), 5UL);
            DungeonEdge wide = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Corridor && CorridorWidth(e) >= 2);
            int w = CorridorWidth(wide);

            Assert.Equal(2 * w, wide.Doors.Count);
            Assert.All(wide.Doors, t => Assert.Equal(DungeonCellKind.DoorFrame, layout.GetCell(t.X, t.Z, t.Floor)));

            // The two ends each carry w door cells, and each end's band is orthogonally adjacent to its room's
            // interior floor (the opening actually connects).
            var rooms = layout.Rooms.ToDictionary(r => r.Id);
            DungeonRoom a = rooms[wide.RoomA];
            DungeonRoom b = rooms[wide.RoomB];
            Assert.Contains(wide.Doors, d => AdjacentToRoomInterior(layout, d, a));
            Assert.Contains(wide.Doors, d => AdjacentToRoomInterior(layout, d, b));
        }

        [Fact]
        public void WideConfig_StaysSolvable_Connected_AndWallInvariantHolds()
        {
            DungeonLayout layout = DungeonGenerator.Generate(WideConfig(), 5UL);

            Assert.True(DungeonSolver.Verify(layout).IsSolvable);
            AssertAllRoomsConnected(layout);
            AssertWallInvariant(layout);
        }

        // Growth adds exactly one tree edge per placed room (none for the entrance), and LoopPlanner appends its
        // loop edges after, so the edges beyond the first (RoomsPlaced - 1) are the loop edges.
        static IEnumerable<DungeonEdge> LoopEdges(DungeonLayout layout) => layout.Edges.Skip(layout.Rooms.Count - 1);

        static DungeonConfig WideLoopConfig() => new()
        {
            RoomCountTarget = 20,
            RoomMinTiles = 4,
            RoomMaxTiles = 8,
            MaxFloors = 1,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 6,
            PlotWidthTiles = 80,
            PlotDepthTiles = 80,
            CorridorMinWidth = 1,
            CorridorMaxWidth = 4,
        };

        [Fact]
        public void WideConfig_ProducesWideLoopEdges_AcrossSeeds()
        {
            bool anyWideLoop = false;
            for (ulong seed = 0; seed < 40 && !anyWideLoop; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(WideLoopConfig(), seed);
                Assert.True(DungeonSolver.Verify(layout).IsSolvable, $"seed {seed} unsolvable");
                if (LoopEdges(layout).Any(e => e.Kind == DungeonEdgeKind.Corridor && CorridorWidth(e) >= 2))
                {
                    anyWideLoop = true;
                }
            }

            Assert.True(anyWideLoop, "no wide loop-edge corridor was produced across the seed range");
        }

        [Fact]
        public void WideLoopEdges_AreValidBands_SolvableAndWalled()
        {
            for (ulong seed = 0; seed < 20; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(WideLoopConfig(), seed);
                Assert.True(DungeonSolver.Verify(layout).IsSolvable, $"seed {seed} unsolvable");

                foreach (DungeonEdge loop in LoopEdges(layout).Where(e => e.Kind == DungeonEdgeKind.Corridor))
                {
                    int w = CorridorWidth(loop);
                    Assert.Equal(2 * w, loop.Doors.Count);
                    Assert.All(loop.Doors, t => Assert.Equal(DungeonCellKind.DoorFrame, layout.GetCell(t.X, t.Z, t.Floor)));
                    Assert.All(loop.Path, t => Assert.Equal(DungeonCellKind.Corridor, layout.GetCell(t.X, t.Z, t.Floor)));
                }

                AssertWallInvariant(layout);
            }
        }

        static bool AdjacentToRoomInterior(DungeonLayout layout, DungeonTile tile, DungeonRoom room)
        {
            foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = tile.X + dx;
                int nz = tile.Z + dz;
                if (tile.Floor == room.Floor
                    && nx >= room.X && nx < room.X + room.Width
                    && nz >= room.Z && nz < room.Z + room.Depth)
                {
                    return true;
                }
            }

            return false;
        }

        static void AssertAllRoomsConnected(DungeonLayout layout)
        {
            var adjacency = new Dictionary<int, List<int>>();
            foreach (DungeonRoom room in layout.Rooms) adjacency[room.Id] = new List<int>();
            foreach (DungeonEdge edge in layout.Edges) { adjacency[edge.RoomA].Add(edge.RoomB); adjacency[edge.RoomB].Add(edge.RoomA); }
            var seen = new HashSet<int> { layout.Rooms[0].Id };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
                foreach (int next in adjacency[queue.Dequeue()])
                    if (seen.Add(next)) queue.Enqueue(next);
            Assert.Equal(layout.Rooms.Count, seen.Count);
        }

        static void AssertWallInvariant(DungeonLayout layout)
        {
            for (int f = 0; f < layout.Floors; f++)
                for (int x = 0; x < layout.Width; x++)
                    for (int z = 0; z < layout.Depth; z++)
                    {
                        if (!DungeonLayout.IsWalkable(layout.GetCell(x, z, f))) continue;
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                                Assert.NotEqual(DungeonCellKind.Empty, layout.GetCell(x + dx, z + dz, f));
                    }
        }
    }
}
