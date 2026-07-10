using System;
using System.Linq;
using System.Numerics;
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

        [Fact]
        public void Emit_DirectionalYaw_FacesWorldDirection_UnderPlotYaw()
        {
            // Binds the yaw CONVENTION, not just a number: applying the same rotation placement
            // consumers apply (Quaternion.CreateFromAxisAngle(UnitY, Yaw), e.g. ChunkStatics) to the
            // piece's authored local +Z must reproduce the true world direction of the run, derived
            // independently from plot.TileCenter. Plot yaw pi/3 is deliberately not a multiple of
            // pi/2, so a sign error in the plot-yaw composition cannot cancel out.
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(3f, -7f, 0f, MathF.PI / 3f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            // EmitStairRuns walks layout.Edges in order, so the nth dungeon_stair placement pairs
            // with the nth stair edge.
            var stairEdges = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            var stairPlacements = target.Placements.Where(p => p.Kind == "dungeon_stair").ToList();
            Assert.NotEmpty(stairEdges);
            Assert.Equal(stairEdges.Count, stairPlacements.Count);

            DungeonEdge stairEdge = stairEdges[0];
            MapPlacement stair = stairPlacements[0];

            (float lx, _, float lz) = plot.TileCenter(stairEdge.Path[0], layout.CellSizeMeters, layout.FloorHeightMeters);
            (float ux, _, float uz) = plot.TileCenter(stairEdge.Path[1], layout.CellSizeMeters, layout.FloorHeightMeters);
            Vector2 runDir = Vector2.Normalize(new Vector2(ux - lx, uz - lz));

            Vector3 stairFacing = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, stair.Yaw));
            Assert.Equal(runDir.X, stairFacing.X, 3);
            Assert.Equal(runDir.Y, stairFacing.Z, 3);

            // Same binding for a door frame: its local +Z must map onto the passage direction of the
            // edge whose Doors pair it heads (Doors[0] -> Doors[1] is colinear with the whole run).
            DungeonEdge corridor = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Corridor);
            (float ax, _, float az) = plot.TileCenter(corridor.Doors[0], layout.CellSizeMeters, layout.FloorHeightMeters);
            (float bx, _, float bz) = plot.TileCenter(corridor.Doors[1], layout.CellSizeMeters, layout.FloorHeightMeters);
            Vector2 passDir = Vector2.Normalize(new Vector2(bx - ax, bz - az));

            MapPlacement door = target.Placements.Single(p => p.Kind == "dungeon_doorframe"
                && MathF.Abs(p.X - ax) < 1e-3f && MathF.Abs(p.Z - az) < 1e-3f);
            Vector3 doorFacing = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, door.Yaw));
            Assert.Equal(passDir.X, doorFacing.X, 3);
            Assert.Equal(passDir.Y, doorFacing.Z, 3);
        }

        [Fact]
        public void Emit_AppendsWithoutClearingExistingContent()
        {
            DungeonLayout layout = SingleFloorLayoutWithSpawns();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            var target = new MapDocument();
            target.Placements.Add(new MapPlacement { Id = "pre-placement", Kind = "oak_tree", X = 1f, Z = 2f });
            target.Spawns.Add(new MapSpawn { Id = "pre-spawn", ArchetypeId = "villager", X = 3f, Z = 4f });
            target.Regions.Add(new MapRegion { Name = "pre-region", Shape = new DiscShapeDoc { Radius = 1f } });

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            Assert.Equal("pre-placement", target.Placements[0].Id);
            Assert.Equal("oak_tree", target.Placements[0].Kind);
            Assert.Equal("pre-spawn", target.Spawns[0].Id);
            Assert.Equal("villager", target.Spawns[0].ArchetypeId);
            Assert.Equal("pre-region", target.Regions[0].Name);
            Assert.True(target.Placements.Count > 1);
            Assert.True(target.Spawns.Count > 1);
            Assert.True(target.Regions.Count > 1);
        }
    }
}
