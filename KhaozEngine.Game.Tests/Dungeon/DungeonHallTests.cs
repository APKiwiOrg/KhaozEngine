using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Content;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // Hall room type: elongated grand connectors placed by growth when HallChancePercent is positive. A hall's
    // long axis (up to HallMaxLengthTiles) runs along the corridor that reached it; its short-axis girth is a
    // normal room span, so a hall is provably longer than any RoomMaxTiles room.
    public class DungeonHallTests
    {
        static DungeonConfig HallConfig() => new()
        {
            RoomCountTarget = 24,
            RoomMinTiles = 4,
            RoomMaxTiles = 8,
            MaxFloors = 1,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
            PlotWidthTiles = 90,
            PlotDepthTiles = 90,
            HallChancePercent = 60,
            HallMinLengthTiles = 10,
            HallMaxLengthTiles = 16,
        };

        [Fact]
        public void HallConfig_PlacesElongatedHalls()
        {
            DungeonLayout layout = DungeonGenerator.Generate(HallConfig(), 5UL);

            List<DungeonRoom> halls = layout.Rooms.Where(r => r.RoomType == DungeonRoomType.Hall).ToList();
            Assert.NotEmpty(halls);

            foreach (DungeonRoom hall in halls)
            {
                int longAxis = Math.Max(hall.Width, hall.Depth);
                int girth = Math.Min(hall.Width, hall.Depth);
                Assert.InRange(longAxis, 10, 16);   // long axis in [HallMinLengthTiles, HallMaxLengthTiles]
                Assert.InRange(girth, 4, 8);        // girth is a normal room span
            }
        }

        [Fact]
        public void HallConfig_StaysSolvable_Connected_AndWallInvariantHolds()
        {
            DungeonLayout layout = DungeonGenerator.Generate(HallConfig(), 5UL);

            Assert.True(DungeonSolver.Verify(layout).IsSolvable);

            var adjacency = new Dictionary<int, List<int>>();
            foreach (DungeonRoom room in layout.Rooms) adjacency[room.Id] = new List<int>();
            foreach (DungeonEdge edge in layout.Edges) { adjacency[edge.RoomA].Add(edge.RoomB); adjacency[edge.RoomB].Add(edge.RoomA); }
            var seen = new HashSet<int> { layout.Rooms[0].Id };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
                foreach (int next in adjacency[queue.Dequeue()])
                    if (seen.Add(next)) queue.Enqueue(next);
            Assert.Equal(layout.Rooms.Count, seen.Count);

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
        public void HallLayout_SchemaValidates()
        {
            DungeonLayout layout = DungeonGenerator.Generate(HallConfig(), 5UL);
            Assert.Contains(layout.Rooms, r => r.RoomType == DungeonRoomType.Hall);

            string json = DungeonJson.SaveLayout(layout);
            ValidationReport report = JsonSchemaValidator.Validate(json, DungeonSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }
    }
}
