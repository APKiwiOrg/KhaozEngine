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
