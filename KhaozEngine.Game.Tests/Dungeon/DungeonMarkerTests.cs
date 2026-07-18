using System.Collections.Generic;
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

            // Spawn and loot tiles are drawn per room without replacement, so within any one room no two of
            // them may share a tile. Pins PickDistinctInteriorTiles. Grouping keys on the containing room
            // (interiors never overlap, so Single is safe). Entrance/objective center markers are outside the
            // distinct set by design and excluded here.
            var perRoom = layout.Markers
                .Where(m => m.Type == DungeonMarkerType.Spawn || m.Type == DungeonMarkerType.Loot)
                .GroupBy(m => layout.Rooms.Single(room =>
                    room.Floor == m.Tile.Floor
                    && m.Tile.X >= room.X && m.Tile.X < room.X + room.Width
                    && m.Tile.Z >= room.Z && m.Tile.Z < room.Z + room.Depth).Id);
            foreach (var group in perRoom)
            {
                List<DungeonTile> tiles = group.Select(m => m.Tile).ToList();
                Assert.Equal(tiles.Count, tiles.Distinct().Count());
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

            // And the marker phase must actually respond to the config change, so a regression that ignores
            // SpawnMarkersPerRoomMax cannot pass vacuously: the max=3 config yields spawn markers, the max=0
            // config yields none (stronger than a bare marker-count inequality, which could collide).
            Assert.Contains(a.Markers, m => m.Type == DungeonMarkerType.Spawn);
            Assert.DoesNotContain(b.Markers, m => m.Type == DungeonMarkerType.Spawn);
        }

        [Fact]
        public void KeyRoomLootMarkers_CarryTreasureTag()
        {
            // The default-config-shaped case the review flagged: LockCount=1 places a Key room, whose loot
            // markers must carry the extra "treasure" tag. A room's loot count draw can legitimately be zero
            // (markers.Next(LootMarkersPerRoomMax + 1)), so not every seed drops a loot marker in the Key
            // room. Scan seeds deterministically for the first layout whose Key room holds one (the same
            // pattern as DungeonFloorsTests.StairLayout) and pin the tag on every loot marker found there.
            // Throws if no seed in the range qualifies, which is itself a regression signal.
            var config = new DungeonConfig
            {
                RoomCountTarget = 14,
                MaxFloors = 1,
                LockCount = 1,
                BossRoom = true,
                LoopEdgeBudget = 2,
            };

            for (ulong seed = 1; seed <= 50; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);
                DungeonRoom? keyRoom = layout.Rooms.FirstOrDefault(r => r.RoomType == DungeonRoomType.Key);
                if (keyRoom is null)
                {
                    continue;
                }

                List<DungeonMarker> keyRoomLoot = layout.Markers
                    .Where(m => m.Type == DungeonMarkerType.Loot
                        && m.Tile.Floor == keyRoom.Floor
                        && m.Tile.X >= keyRoom.X && m.Tile.X < keyRoom.X + keyRoom.Width
                        && m.Tile.Z >= keyRoom.Z && m.Tile.Z < keyRoom.Z + keyRoom.Depth)
                    .ToList();
                if (keyRoomLoot.Count == 0)
                {
                    continue;
                }

                Assert.All(keyRoomLoot, m => Assert.Contains("treasure", m.Tags));
                return;
            }

            throw new Xunit.Sdk.XunitException("No seed in 1..50 produced a Key room containing a loot marker.");
        }
    }
}
