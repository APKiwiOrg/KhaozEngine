using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonGeneratorTests
    {
        static DungeonConfig Config() => new() { RoomCountTarget = 10, MaxFloors = 1, LockCount = 0, BossRoom = false, LoopEdgeBudget = 0 };

        [Fact]
        public void SameSeed_SameLayoutHash()
        {
            var a = DungeonGenerator.Generate(Config(), 42UL);
            var b = DungeonGenerator.Generate(Config(), 42UL);
            Assert.Equal(a.LayoutHash(), b.LayoutHash());
        }

        [Fact]
        public void DifferentSeed_DifferentLayoutHash()
        {
            var a = DungeonGenerator.Generate(Config(), 42UL);
            var b = DungeonGenerator.Generate(Config(), 43UL);
            Assert.NotEqual(a.LayoutHash(), b.LayoutHash());
        }

        [Fact]
        public void AllRoomsConnected_ByBfsOverEdges()
        {
            var layout = DungeonGenerator.Generate(Config(), 7UL);
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var room in layout.Rooms) adjacency[room.Id] = new List<int>();
            foreach (var edge in layout.Edges) { adjacency[edge.RoomA].Add(edge.RoomB); adjacency[edge.RoomB].Add(edge.RoomA); }
            var seen = new HashSet<int> { layout.Rooms[0].Id };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
                foreach (var next in adjacency[queue.Dequeue()])
                    if (seen.Add(next)) queue.Enqueue(next);
            Assert.Equal(layout.Rooms.Count, seen.Count);
        }

        [Fact]
        public void NoWalkableCell_TouchesEmpty_AfterWallPass()
        {
            var layout = DungeonGenerator.Generate(Config(), 7UL);
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

        [Fact]
        public void TinyPlot_DegradesGracefully()
        {
            var config = Config();
            config.PlotWidthTiles = 12; config.PlotDepthTiles = 12; config.RoomCountTarget = 30;
            var layout = DungeonGenerator.Generate(config, 3UL);
            Assert.True(layout.Stats.RoomsPlaced < 30);
            Assert.True(layout.Stats.Saturated);
            Assert.True(layout.Rooms.Count >= 1);
        }
    }
}
