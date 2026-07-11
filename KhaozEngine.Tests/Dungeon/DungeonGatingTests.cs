using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonGatingTests
    {
        static DungeonConfig GatingConfig(int lockCount, int floors = 1) => new()
        {
            RoomCountTarget = 14,
            MaxFloors = floors,
            LockCount = lockCount,
            BossRoom = true,
            LoopEdgeBudget = 2,
        };

        // BFS room-graph distances from the entrance over every edge (locks ignored), the shared basis for the
        // boss and lock-placement expectations below.
        static Dictionary<int, int> RoomDistances(DungeonLayout layout)
        {
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

            int entranceId = layout.Rooms.First(r => r.RoomType == DungeonRoomType.Entrance).Id;
            var distances = new Dictionary<int, int> { [entranceId] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(entranceId);
            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                foreach (int next in adjacency[id])
                {
                    if (!distances.ContainsKey(next))
                    {
                        distances[next] = distances[id] + 1;
                        queue.Enqueue(next);
                    }
                }
            }

            return distances;
        }

        // True when room B is reachable from the entrance over the room graph with the single edge carrying
        // lockId removed. Proves a key placed for that lock does not sit behind the lock it opens.
        static bool ReachableWithLockRemoved(DungeonLayout layout, int lockId, int roomId)
        {
            var adjacency = new Dictionary<int, List<int>>();
            foreach (DungeonRoom room in layout.Rooms)
            {
                adjacency[room.Id] = new List<int>();
            }

            foreach (DungeonEdge edge in layout.Edges)
            {
                if (edge.LockId.HasValue && edge.LockId.Value == lockId)
                {
                    continue;
                }

                adjacency[edge.RoomA].Add(edge.RoomB);
                adjacency[edge.RoomB].Add(edge.RoomA);
            }

            int entranceId = layout.Rooms.First(r => r.RoomType == DungeonRoomType.Entrance).Id;
            var seen = new HashSet<int> { entranceId };
            var queue = new Queue<int>();
            queue.Enqueue(entranceId);
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

            return seen.Contains(roomId);
        }

        [Fact]
        public void Locks_Placed_UpTo_Budget()
        {
            DungeonLayout layout = DungeonGenerator.Generate(GatingConfig(lockCount: 2), 7UL);

            Assert.True(layout.Stats.LocksPlaced >= 1, "expected at least one lock placed");
            Assert.True(layout.Stats.LocksPlaced <= 2, "must not exceed the requested budget");
            Assert.Equal(2, layout.Stats.LocksRequested);

            List<int> lockedIds = layout.Edges
                .Where(e => e.LockId.HasValue)
                .Select(e => e.LockId!.Value)
                .ToList();

            Assert.Equal(layout.Stats.LocksPlaced, lockedIds.Count);
            Assert.Equal(lockedIds.Count, lockedIds.Distinct().Count());

            // Every locked edge is opened by exactly one key, and every key opens a real locked edge.
            Assert.Equal(lockedIds.Count, layout.Keys.Count);
            Assert.Equal(
                lockedIds.OrderBy(i => i).ToList(),
                layout.Keys.Select(k => k.LockId).OrderBy(i => i).ToList());
        }

        [Fact]
        public void KeyRoom_NotBehind_OwnLock()
        {
            DungeonLayout layout = DungeonGenerator.Generate(GatingConfig(lockCount: 2), 7UL);

            Assert.NotEmpty(layout.Keys);
            foreach (DungeonKeyPlacement key in layout.Keys)
            {
                Assert.True(
                    ReachableWithLockRemoved(layout, key.LockId, key.RoomId),
                    $"key for lock {key.LockId} in room {key.RoomId} is behind the lock it opens");
            }
        }

        [Fact]
        public void Boss_IsFarthest()
        {
            DungeonLayout layout = DungeonGenerator.Generate(GatingConfig(lockCount: 2), 7UL);

            DungeonRoom boss = Assert.Single(layout.Rooms, r => r.RoomType == DungeonRoomType.Boss);
            Dictionary<int, int> distances = RoomDistances(layout);

            int maxDistance = distances.Values.Max();
            Assert.Equal(maxDistance, distances[boss.Id]);

            // Tie-break is the lowest id among the farthest rooms.
            int expectedBossId = distances.Where(p => p.Value == maxDistance).Min(p => p.Key);
            Assert.Equal(expectedBossId, boss.Id);
        }

        [Fact]
        public void SweepSeeds_AllSolvable()
        {
            for (ulong seed = 0; seed < 100; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(GatingConfig(lockCount: 2, floors: 2), seed);
                DungeonSolveReport report = DungeonSolver.Verify(layout);
                Assert.True(report.IsSolvable, $"seed {seed} produced an unsolvable layout: {string.Join(" ", report.Errors)}");
            }
        }

        [Fact]
        public void KeyBehindOwnLock_FailsSolver()
        {
            // Hand-built minimal single-floor line: entrance(0) - r1 - r2, with the r1->r2 edge locked and its
            // key wrongly placed inside r2 (behind the very lock it opens). The belt-and-braces structural check
            // must reject it with its own distinct message, independent of the flood-fill reachability proof.
            const int width = 20;
            const int depth = 6;
            var layout = new DungeonLayout(width, depth, 1, 2f, 4f)
            {
                Rooms = new[]
                {
                    new DungeonRoom { Id = 0, Floor = 0, X = 1, Z = 1, Width = 3, Depth = 3, RoomType = DungeonRoomType.Entrance },
                    new DungeonRoom { Id = 1, Floor = 0, X = 7, Z = 1, Width = 3, Depth = 3, RoomType = DungeonRoomType.Normal },
                    new DungeonRoom { Id = 2, Floor = 0, X = 13, Z = 1, Width = 3, Depth = 3, RoomType = DungeonRoomType.Normal },
                },
                Edges = new[]
                {
                    new DungeonEdge
                    {
                        RoomA = 0,
                        RoomB = 1,
                        Kind = DungeonEdgeKind.Corridor,
                        Path = new[] { new DungeonTile(5, 2, 0) },
                        Doors = new[] { new DungeonTile(4, 2, 0), new DungeonTile(6, 2, 0) },
                    },
                    new DungeonEdge
                    {
                        RoomA = 1,
                        RoomB = 2,
                        Kind = DungeonEdgeKind.Corridor,
                        Path = new[] { new DungeonTile(11, 2, 0) },
                        Doors = new[] { new DungeonTile(10, 2, 0), new DungeonTile(12, 2, 0) },
                        LockId = 1,
                    },
                },
                Keys = new[] { new DungeonKeyPlacement { LockId = 1, RoomId = 2 } },
                Markers = System.Array.Empty<DungeonMarker>(),
                Stats = new LayoutStats(),
            };

            void Set(int x, int z, DungeonCellKind kind) => layout.CellsMutable[(0 * depth + z) * width + x] = kind;

            for (int x = 1; x <= 3; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    Set(x, z, DungeonCellKind.RoomFloor);
                }
            }

            for (int x = 7; x <= 9; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    Set(x, z, DungeonCellKind.RoomFloor);
                }
            }

            for (int x = 13; x <= 15; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    Set(x, z, DungeonCellKind.RoomFloor);
                }
            }

            Set(4, 2, DungeonCellKind.DoorFrame);
            Set(5, 2, DungeonCellKind.Corridor);
            Set(6, 2, DungeonCellKind.DoorFrame);
            Set(10, 2, DungeonCellKind.DoorFrame);
            Set(11, 2, DungeonCellKind.Corridor);
            Set(12, 2, DungeonCellKind.DoorFrame);

            DungeonSolveReport report = DungeonSolver.Verify(layout);

            Assert.False(report.IsSolvable);
            Assert.Contains(report.Errors, e => e.Contains("closed region"));
        }
    }
}
