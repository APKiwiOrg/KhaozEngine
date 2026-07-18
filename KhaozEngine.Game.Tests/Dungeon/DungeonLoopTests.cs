using System.Collections.Generic;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonLoopTests
    {
        static DungeonConfig LoopConfig(int budget) => new()
        {
            MaxFloors = 1,
            RoomCountTarget = 14,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = budget,
        };

        [Fact]
        public void LoopBudget_Respected()
        {
            DungeonConfig config = LoopConfig(3);
            DungeonLayout layout = DungeonGenerator.Generate(config, 42UL);
            Assert.True(layout.Edges.Count <= layout.Rooms.Count - 1 + config.LoopEdgeBudget);
        }

        [Fact]
        public void LoopEdges_CreateCycles()
        {
            // Seed 3 is the first of 1..10 whose loop planner commits at least one loop edge for this
            // config, so the test exercises a real cycle rather than passing vacuously.
            DungeonConfig config = LoopConfig(3);
            DungeonLayout layout = DungeonGenerator.Generate(config, 3UL);
            Assert.True(layout.Edges.Count > layout.Rooms.Count - 1);
        }

        [Fact]
        public void Connectivity_StillHolds()
        {
            DungeonConfig config = LoopConfig(3);
            DungeonLayout layout = DungeonGenerator.Generate(config, 3UL);

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
        public void WallPass_StillHolds()
        {
            DungeonConfig config = LoopConfig(3);
            DungeonLayout layout = DungeonGenerator.Generate(config, 3UL);

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
