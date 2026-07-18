using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for the map-edit session document core: open/create/save/validate,
    /// the summary counts, the dirty flag, and the cached terrain field's invalidation rules.</summary>
    public class MapEditSessionTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Open_LoadsDocumentAndReportsSummary()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                var session = new MapEditSession();
                OpenResult result = session.Open(path);

                Assert.Equal(path, result.Path);
                Assert.Equal("test-zone", result.Id);
                Assert.Equal("Test Zone", result.DisplayName);

                MapSummary s = session.Summary();
                Assert.Equal(new[] { "lake", "flatten" }, s.FeatureTypes);
                Assert.Equal(new[] { "trees" }, s.ScatterLayers);
                Assert.Equal(new[] { "understory" }, s.CompanionLayers);
                Assert.Equal(1, s.ExclusionCount);
                Assert.Equal(1, s.ScatterOverrideCount);
                Assert.Equal(1, s.PlacementCount);
                Assert.Equal(1, s.SpawnCount);
                Assert.Equal(0, s.PlayerSpawnCount);
                Assert.Empty(s.PlayerSpawnIds);
                Assert.Equal(new[] { "town" }, s.RegionNames);
                Assert.Equal(-100f, s.MinX);
                Assert.Equal(100f, s.MaxZ);
                Assert.False(s.Dirty);
                Assert.False(session.IsDirty);
                Assert.True(session.HasDocument);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void MapSummary_ReportsPlayerSpawns()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                var session = new MapEditSession();
                session.Open(path);

                Assert.Equal(0, session.Summary().PlayerSpawnCount);
                Assert.Empty(session.Summary().PlayerSpawnIds);

                session.Mutate((d, r) =>
                {
                    d.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1", X = 0f, Z = 0f });
                    d.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-2", X = 5f, Z = 5f });
                    return 0;
                }, worldChanged: false);

                MapSummary s = session.Summary();
                Assert.Equal(2, s.PlayerSpawnCount);
                Assert.Equal(new[] { "player-1", "player-2" }, s.PlayerSpawnIds);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Open_MissingFile_ThrowsWithPath()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "does-not-exist.map.json");
                var session = new MapEditSession();

                MapDocumentException ex = Assert.Throws<MapDocumentException>(() => session.Open(path));
                Assert.Contains(path, ex.Message);
                Assert.False(session.HasDocument);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Create_WritesValidDocumentAndOpensIt()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "nested", "new.map.json");
                var session = new MapEditSession();

                OpenResult result = session.Create(path, "brand-new", "Brand New",
                    minX: -64f, minZ: -48f, maxX: 64f, maxZ: 48f, seed: 5, waterLevel: -1f);

                Assert.True(File.Exists(path));
                Assert.Equal("brand-new", result.Id);
                Assert.True(session.HasDocument);
                Assert.False(session.IsDirty);

                MapDocument reloaded = MapDocumentFile.Load(path);
                Assert.Equal("brand-new", reloaded.Id);
                Assert.Equal("Brand New", reloaded.DisplayName);
                Assert.Equal(-64f, reloaded.Bounds.MinX);
                Assert.Equal(-48f, reloaded.Bounds.MinZ);
                Assert.Equal(64f, reloaded.Bounds.MaxX);
                Assert.Equal(48f, reloaded.Bounds.MaxZ);
                Assert.Equal(5, reloaded.Terrain.Seed);
                Assert.Equal(-1f, reloaded.Terrain.WaterLevel);
                MapBiomeBand band = Assert.Single(reloaded.Terrain.Biomes);
                Assert.Equal(BiomeId.Meadow, band.Biome);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Create_ExistingFileWithoutOverwrite_Throws()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "exists.map.json");
                var session = new MapEditSession();
                session.Create(path, "one", "One", -10f, -10f, 10f, 10f);

                var session2 = new MapEditSession();
                Assert.Throws<IOException>(() =>
                    session2.Create(path, "two", "Two", -10f, -10f, 10f, 10f, overwrite: false));

                // With overwrite it succeeds.
                OpenResult ok = session2.Create(path, "two", "Two", -10f, -10f, 10f, 10f, overwrite: true);
                Assert.Equal("two", ok.Id);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Summary_WithoutOpenDocument_Throws()
        {
            var session = new MapEditSession();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => session.Summary());
            Assert.Contains("map_open", ex.Message);
        }

        [Fact]
        public void Save_ClearsDirty_AfterMutate()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                var session = new MapEditSession();
                session.Open(path);

                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "x", Kind = "pine_a", X = 1f, Z = 1f });
                    return 0;
                }, worldChanged: false);

                Assert.True(session.IsDirty);

                SaveResult save = session.Save();
                Assert.True(save.Saved);
                Assert.Equal(path, save.Path);
                Assert.False(session.IsDirty);

                MapDocument reloaded = MapDocumentFile.Load(path);
                Assert.Contains(reloaded.Placements, p => p.Id == "x");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Validate_ReportsStructuralAndSchema()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                var session = new MapEditSession();
                session.Open(path);

                ValidateResult ok = session.Validate();
                Assert.True(ok.StructuralValid);
                Assert.Empty(ok.StructuralErrors);
                Assert.True(ok.SchemaValid);
                Assert.Empty(ok.SchemaErrors);

                // Bypass command guards: inject a duplicate placement id straight into the document.
                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "inn", Kind = "building_inn", X = 5f, Z = 5f });
                    return 0;
                }, worldChanged: false);

                ValidateResult bad = session.Validate();
                Assert.False(bad.StructuralValid);
                Assert.Contains(bad.StructuralErrors, e => e.Contains("duplicate placement id"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Field_IsCachedUntilWorldChange()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                var session = new MapEditSession();
                session.Open(path);

                TerrainField first = session.Field();
                Assert.Same(first, session.Field());

                // A non-world mutation keeps the cached field.
                session.Mutate((d, r) => 0, worldChanged: false);
                Assert.Same(first, session.Field());

                // A world mutation invalidates it.
                session.Mutate((d, r) => { d.Terrain.WaterLevel = 3f; return 0; }, worldChanged: true);
                Assert.NotSame(first, session.Field());
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
