using System;
using System.Linq;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonMapDocEmitterTests
    {
        static DungeonConfig FloorsConfig() => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        static DungeonConfig SingleFloorConfig() => new()
        {
            MaxFloors = 1,
            RoomCountTarget = 8,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        // Same scan pattern as DungeonFloorsTests.StairLayout: the first seed in this range whose growth
        // reaches an upper floor, so multi-floor assertions always exercise real cross-floor content
        // instead of passing vacuously. Throws if none of the seeds qualify, itself a regression signal.
        static DungeonLayout MultiFloorLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Rooms.Any(r => r.Floor > 0))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No seed in 11..60 grew onto an upper floor.");
        }

        // Same scan, additionally requiring a Spawn marker on an upper floor so the floor-tag assertion
        // exercises a real cross-floor spawn rather than only floor-0 spawns.
        static DungeonLayout MultiFloorLayoutWithUpperSpawn()
        {
            DungeonConfig config = FloorsConfig();
            config.SpawnMarkersPerRoomMax = 3;

            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(config, seed);
                if (layout.Rooms.Any(r => r.Floor > 0)
                    && layout.Markers.Any(m => m.Type == DungeonMarkerType.Spawn && m.Tile.Floor > 0))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No seed in 11..60 produced an upper-floor spawn marker.");
        }

        [Fact]
        public void Emit_PlacementsHaveExplicitY_PerFloor()
        {
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 10f, 0f);
            var target = new MapDocument { Id = "test-zone", DisplayName = "Test Zone" };

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            Assert.NotEmpty(target.Placements);
            Assert.All(target.Placements, p => Assert.True(p.Y.HasValue));
            Assert.Contains(target.Placements, p => p.Y!.Value == 4f * 1 + 10f);
        }

        [Fact]
        public void Emit_MissingKitPiece_Throws_NamingPiece()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloorConfig(), 1UL);
            var kit = new DungeonKitMap();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            var target = new MapDocument();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => DungeonMapDocEmitter.Emit(layout, kit, plot, target));

            Assert.Matches("No kit id mapped for dungeon piece '\\w+'\\.", ex.Message);
        }

        // The first seed in the scanned range whose single-floor layout carries at least one Spawn marker,
        // so spawn-bearing tests never pass vacuously (a room's spawn count draw can legitimately be zero).
        static DungeonLayout SingleFloorLayoutWithSpawns()
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(SingleFloorConfig(), seed);
                if (layout.Markers.Any(m => m.Type == DungeonMarkerType.Spawn))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No seed in 1..50 produced a spawn marker.");
        }

        [Fact]
        public void Emit_RoundTrips_ThroughMapDocumentFile()
        {
            DungeonLayout layout = SingleFloorLayoutWithSpawns();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(5f, -5f, 0f, 0f);
            var target = new MapDocument
            {
                Id = "test-zone",
                DisplayName = "Test Zone",
                Bounds = new MapBounds { MinX = -1f, MinZ = -1f, MaxX = 1f, MaxZ = 1f },
            };

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            string json = MapDocumentFile.SaveText(target);
            MapDocument loaded = MapDocumentFile.LoadText(json);

            Assert.NotEmpty(target.Placements);
            Assert.Equal(target.Placements.Count, loaded.Placements.Count);

            MapPlacement firstOut = target.Placements[0];
            MapPlacement firstIn = loaded.Placements[0];
            Assert.Equal(firstOut.Id, firstIn.Id);
            Assert.Equal(firstOut.Kind, firstIn.Kind);
            Assert.Equal(firstOut.X, firstIn.X, 3);
            Assert.NotNull(firstOut.Y);
            Assert.NotNull(firstIn.Y);
            Assert.Equal(firstOut.Y!.Value, firstIn.Y!.Value, 3);
            Assert.Equal(firstOut.Z, firstIn.Z, 3);
            Assert.Equal(firstOut.Yaw, firstIn.Yaw, 3);

            MapPlacement lastOut = target.Placements[^1];
            MapPlacement lastIn = loaded.Placements[^1];
            Assert.Equal(lastOut.Id, lastIn.Id);
            Assert.Equal(lastOut.Kind, lastIn.Kind);
            Assert.Equal(lastOut.X, lastIn.X, 3);
            Assert.NotNull(lastOut.Y);
            Assert.NotNull(lastIn.Y);
            Assert.Equal(lastOut.Y!.Value, lastIn.Y!.Value, 3);
            Assert.Equal(lastOut.Z, lastIn.Z, 3);
            Assert.Equal(lastOut.Yaw, lastIn.Yaw, 3);

            // Placement id + tag rule from the task brief.
            Assert.All(target.Placements, p => Assert.Matches("^dungeon-[0-9a-f]{8}-\\d+$", p.Id));
            Assert.All(target.Placements, p => Assert.Contains("dungeon", p.Tags));

            // The document really contains spawns, and they survive the save/load with the default
            // placeholder archetype id intact, proving the validator's non-empty-archetype rule is
            // satisfied for the common (spawn-bearing) case.
            Assert.NotEmpty(target.Spawns);
            Assert.Equal(target.Spawns.Count, loaded.Spawns.Count);
            Assert.All(loaded.Spawns, s => Assert.Equal("dungeon-spawn", s.ArchetypeId));
        }

        [Fact]
        public void Emit_CustomSpawnArchetypeId_FlowsThrough()
        {
            DungeonLayout layout = SingleFloorLayoutWithSpawns();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target, "goblin-scout");

            Assert.NotEmpty(target.Spawns);
            Assert.All(target.Spawns, s => Assert.Equal("goblin-scout", s.ArchetypeId));

            Assert.Throws<ArgumentException>(
                () => DungeonMapDocEmitter.Emit(layout, kit, plot, new MapDocument(), " "));
        }

        [Fact]
        public void Emit_AddsFlattenAndBounds()
        {
            DungeonConfig config = SingleFloorConfig();
            config.RoomCountTarget = 4;
            DungeonLayout layout = DungeonGenerator.Generate(config, 5UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 2f, 0f);
            var target = new MapDocument
            {
                Id = "test-zone",
                DisplayName = "Test Zone",
                Bounds = new MapBounds { MinX = -1f, MinZ = -1f, MaxX = 1f, MaxZ = 1f },
            };

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            FlattenFeatureDoc flatten = Assert.Single(target.Terrain.Features.OfType<FlattenFeatureDoc>());
            float plotWidth = layout.Width * layout.CellSizeMeters;
            float plotDepth = layout.Depth * layout.CellSizeMeters;
            Assert.Equal(plotWidth / 2f, flatten.CenterX, 3);
            Assert.Equal(plotDepth / 2f, flatten.CenterZ, 3);
            Assert.Equal(0.5f * MathF.Sqrt(plotWidth * plotWidth + plotDepth * plotDepth), flatten.Radius, 3);
            Assert.Equal(2f, flatten.TargetHeight, 3);

            Assert.True(target.Bounds.MinX <= 0f);
            Assert.True(target.Bounds.MinZ <= 0f);
            Assert.True(target.Bounds.MaxX >= plotWidth);
            Assert.True(target.Bounds.MaxZ >= plotDepth);
        }

        [Fact]
        public void Emit_WallCount_MatchesGrid()
        {
            DungeonConfig config = SingleFloorConfig();
            config.RoomCountTarget = 8;
            DungeonLayout layout = DungeonGenerator.Generate(config, 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            int wallCells = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        if (layout.GetCell(x, z, f) == DungeonCellKind.Wall)
                        {
                            wallCells++;
                        }
                    }
                }
            }

            int wallPlacements = target.Placements.Count(p => p.Kind == "dungeon_wall");
            Assert.True(wallCells > 0);
            Assert.Equal(wallCells, wallPlacements);
        }

        [Fact]
        public void Emit_SpawnsCarryFloorTag()
        {
            DungeonLayout layout = MultiFloorLayoutWithUpperSpawn();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            var spawnMarkers = layout.Markers.Where(m => m.Type == DungeonMarkerType.Spawn).ToList();
            Assert.NotEmpty(spawnMarkers);
            Assert.Equal(spawnMarkers.Count, target.Spawns.Count);

            for (int i = 0; i < spawnMarkers.Count; i++)
            {
                MapSpawn spawn = target.Spawns[i];
                int expectedFloor = spawnMarkers[i].Tile.Floor;
                var floorTags = spawn.Tags.Where(t => t.StartsWith("floor:", StringComparison.Ordinal)).ToList();
                Assert.Single(floorTags);
                Assert.Equal($"floor:{expectedFloor}", floorTags[0]);
            }

            Assert.Contains(target.Spawns, s => s.Tags.Contains("floor:1") || s.Tags.Contains("floor:2"));
        }
    }
}
