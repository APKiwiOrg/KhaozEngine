using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonFloorsTests
    {
        static DungeonConfig FloorsConfig() => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        // The first seed in the scanned range whose multi-floor growth carves at least one stair edge, so the
        // stair-shape/connectivity/wall-pass tests always exercise a real cross-floor layout rather than passing
        // vacuously. Throws if none of the seeds produce a stair, which is itself a regression signal.
        static DungeonLayout StairLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No stair edge was produced across seeds 11..60.");
        }

        [Fact]
        public void MultiFloor_UsesUpperFloors()
        {
            // Seed 11 is the first of 11..20 whose growth reaches an upper floor for this config; the loop keeps
            // the test robust if growth tuning shifts the exact first-passing seed.
            bool found = false;
            for (ulong seed = 11; seed <= 20; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Stats.FloorsUsed >= 2)
                {
                    Assert.Contains(layout.Rooms, r => r.Floor > 0);
                    found = true;
                    break;
                }
            }

            Assert.True(found, "No seed in 11..20 grew onto an upper floor.");
        }

        [Fact]
        public void StairEdges_AreConsistent()
        {
            DungeonLayout layout = StairLayout();
            List<DungeonEdge> stairs = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            Assert.NotEmpty(stairs);

            foreach (DungeonEdge edge in stairs)
            {
                // Path is [StairLower, StairMid, StairUpper, StairTop]: three treads on the lower floor plus the
                // landing one cell PAST the top tread on the floor above.
                Assert.Equal(4, edge.Path.Count);
                DungeonTile lower = edge.Path[0];
                DungeonTile mid = edge.Path[1];
                DungeonTile upper = edge.Path[2];
                DungeonTile top = edge.Path[3];

                // The three treads are colinear on the lower floor, one cell apart, in a straight run.
                Assert.Equal(lower.Floor, mid.Floor);
                Assert.Equal(lower.Floor, upper.Floor);
                (int dx, int dz) = (upper.X - mid.X, upper.Z - mid.Z);
                Assert.Equal((mid.X - lower.X, mid.Z - lower.Z), (dx, dz));
                Assert.Equal(1, Math.Abs(dx) + Math.Abs(dz));

                // The landing is one floor up, one cell past the top tread along the run direction.
                Assert.Equal(upper.Floor + 1, top.Floor);
                Assert.Equal(upper.X + dx, top.X);
                Assert.Equal(upper.Z + dz, top.Z);

                Assert.Equal(DungeonCellKind.StairLower, layout.GetCell(lower.X, lower.Z, lower.Floor));
                Assert.Equal(DungeonCellKind.StairMid, layout.GetCell(mid.X, mid.Z, mid.Floor));
                Assert.Equal(DungeonCellKind.StairUpper, layout.GetCell(upper.X, upper.Z, upper.Floor));
                Assert.Equal(DungeonCellKind.StairTop, layout.GetCell(top.X, top.Z, top.Floor));

                // The open shaft: StairVoid directly above every tread on the upper floor.
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(lower.X, lower.Z, lower.Floor + 1));
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(mid.X, mid.Z, mid.Floor + 1));
                Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(upper.X, upper.Z, upper.Floor + 1));
            }
        }

        [Fact]
        public void Connectivity_HoldsAcrossFloors()
        {
            DungeonLayout layout = StairLayout();
            var adjacency = new Dictionary<int, List<int>>();
            foreach (DungeonRoom room in layout.Rooms)
            {
                adjacency[room.Id] = new List<int>();
            }

            foreach (DungeonEdge edge in layout.Edges)
            {
                adjacency[edge.RoomA].Add(edge.RoomB);
                adjacency[edge.RoomB].Add(edge.RoomA);
            }

            var seen = new HashSet<int> { layout.Rooms[0].Id };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
            {
                foreach (int next in adjacency[queue.Dequeue()])
                {
                    if (seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            Assert.Equal(layout.Rooms.Count, seen.Count);
        }

        // Roofed multi-floor scan: the first seed whose growth carves a stair, so the shaft-enclosure test always
        // exercises a real cross-floor shaft. Roofed so the "shaft stays open above" check (no ceiling over a
        // tread beneath a StairVoid) is meaningful rather than vacuous.
        static DungeonLayout RoofedStairLayout()
        {
            DungeonConfig config = new()
            {
                MaxFloors = 3,
                RoomCountTarget = 16,
                LockCount = 0,
                BossRoom = false,
                LoopEdgeBudget = 0,
                CeilingMode = DungeonCeilingMode.Roofed,
            };

            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);
                if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No stair edge was produced across seeds 11..60.");
        }

        // The upper-floor stairwell shaft must be enclosed on its lateral sides so a climber cannot jump out the
        // side near the top: every empty cell 8-adjacent to a StairVoid becomes a wall. The shaft must still be
        // open ABOVE (no ceiling over a tread beneath a StairVoid) and the treads/landing stay walkable.
        [Fact]
        public void StairwellShaft_EnclosedOnSides_StaysOpenAbove()
        {
            DungeonLayout layout = RoofedStairLayout();

            int voidCells = 0;
            int lateralWalls = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        if (layout.GetCell(x, z, f) != DungeonCellKind.StairVoid)
                        {
                            continue;
                        }

                        voidCells++;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0)
                                {
                                    continue;
                                }

                                DungeonCellKind neighbor = layout.GetCell(x + dx, z + dz, f);
                                // No open gap is left beside the shaft: every lateral neighbour is wall, walkable
                                // (a landing/room the shaft opens onto), or more shaft - never empty.
                                Assert.NotEqual(DungeonCellKind.Empty, neighbor);
                                if (neighbor == DungeonCellKind.Wall)
                                {
                                    lateralWalls++;
                                }
                            }
                        }
                    }
                }
            }

            Assert.True(voidCells > 0, "layout must contain a stair shaft to enclose");
            Assert.True(lateralWalls > 0, "the shaft must gain at least some enclosing side walls");

            foreach (DungeonEdge stair in layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair))
            {
                DungeonTile lower = stair.Path[0];
                DungeonTile mid = stair.Path[1];
                DungeonTile upper = stair.Path[2];
                DungeonTile top = stair.Path[3];

                foreach (DungeonTile tread in new[] { lower, mid, upper })
                {
                    // Treads stay walkable and their shaft cutout stays StairVoid (never walled over).
                    Assert.True(DungeonLayout.IsWalkable(layout.GetCell(tread.X, tread.Z, tread.Floor)),
                        $"tread ({tread.X},{tread.Z},{tread.Floor}) must stay walkable");
                    Assert.Equal(DungeonCellKind.StairVoid, layout.GetCell(tread.X, tread.Z, tread.Floor + 1));

                    // Open above: the tread beneath a StairVoid is not roofed, so the shaft is climbable-through.
                    Assert.False(PieceMapper.HasCeiling(layout, tread.X, tread.Z, tread.Floor),
                        $"tread ({tread.X},{tread.Z},{tread.Floor}) under a StairVoid must not be roofed");
                }

                // The emergence landing stays walkable (a climber steps onto solid floor at the top).
                Assert.True(DungeonLayout.IsWalkable(layout.GetCell(top.X, top.Z, top.Floor)),
                    $"stair landing ({top.X},{top.Z},{top.Floor}) must stay walkable");
            }
        }

        [Fact]
        public void WallPass_HoldsPerFloor()
        {
            DungeonLayout layout = StairLayout();
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
                                Assert.NotEqual(DungeonCellKind.Empty, layout.GetCell(x + dx, z + dz, f));
                            }
                        }
                    }
                }
            }
        }
    }
}
