using System.Linq;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonMarkerTests
    {
        static DungeonConfig Config() => new()
        {
            RoomCountTarget = 10,
            MaxFloors = 1,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        [Fact]
        public void Markers_WithinRoomInteriors()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);

            Assert.NotEmpty(layout.Markers);

            foreach (DungeonMarker marker in layout.Markers)
            {
                DungeonTile tile = marker.Tile;
                Assert.Equal(DungeonCellKind.RoomFloor, layout.GetCell(tile.X, tile.Z, tile.Floor));

                bool insideARoom = layout.Rooms.Any(room =>
                    room.Floor == tile.Floor
                    && tile.X >= room.X && tile.X < room.X + room.Width
                    && tile.Z >= room.Z && tile.Z < room.Z + room.Depth);
                Assert.True(insideARoom, $"marker at ({tile.X},{tile.Z},{tile.Floor}) is not inside any room rect");
            }
        }

        [Fact]
        public void EntranceAndBossMarkers_Exist()
        {
            DungeonConfig config = Config();
            config.RoomCountTarget = 14;
            config.BossRoom = true;

            DungeonLayout layout = DungeonGenerator.Generate(config, 7UL);

            DungeonRoom entrance = layout.Rooms.Single(r => r.RoomType == DungeonRoomType.Entrance);
            DungeonRoom boss = layout.Rooms.Single(r => r.RoomType == DungeonRoomType.Boss);

            DungeonMarker entranceMarker = layout.Markers.Single(m => m.Type == DungeonMarkerType.Entrance);
            Assert.Contains("entrance", entranceMarker.Tags);
            Assert.Equal(entrance.Floor, entranceMarker.Tile.Floor);
            Assert.Equal(entrance.X + entrance.Width / 2, entranceMarker.Tile.X);
            Assert.Equal(entrance.Z + entrance.Depth / 2, entranceMarker.Tile.Z);

            DungeonMarker bossMarker = layout.Markers.Single(m => m.Type == DungeonMarkerType.Objective);
            Assert.Contains("boss", bossMarker.Tags);
            Assert.Equal(boss.Floor, bossMarker.Tile.Floor);
            Assert.Equal(boss.X + boss.Width / 2, bossMarker.Tile.X);
            Assert.Equal(boss.Z + boss.Depth / 2, bossMarker.Tile.Z);
        }

        [Fact]
        public void MarkerStream_Isolated()
        {
            DungeonConfig baseline = Config();
            baseline.RoomCountTarget = 14;
            baseline.SpawnMarkersPerRoomMax = 3;

            DungeonConfig varied = Config();
            varied.RoomCountTarget = 14;
            varied.SpawnMarkersPerRoomMax = 0;

            DungeonLayout a = DungeonGenerator.Generate(baseline, 7UL);
            DungeonLayout b = DungeonGenerator.Generate(varied, 7UL);

            // Same rooms/edges/keys, only the marker phase's SpawnMarkersPerRoomMax differs, so the marker
            // stream must not perturb growth or gating: the structural fold (which excludes markers/stats)
            // must be identical even though the two layouts carry different marker sets.
            Assert.Equal(a.StructureHash(), b.StructureHash());
        }
    }
}
