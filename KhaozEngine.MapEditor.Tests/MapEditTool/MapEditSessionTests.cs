using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;
using TiledFixture = KhaozEngine.Tests.MapDoc.TiledDocFixture;

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
                Assert.True(ok.SchemaChecked);
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
                Assert.False(bad.SchemaChecked);   // skipped, not "checked and invalid"
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

        // ---- tiled document form (#334 stage 3): whole-load vs windowed, set_window, convert, retile --------

        [Fact]
        public void Open_TiledDirectoryUnderLimit_LoadsWhole()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var session = new MapEditSession();
                session.Open(dir);

                WindowStatusResult status = session.WindowStatus();
                Assert.True(status.Tiled);
                Assert.False(status.Windowed);
                Assert.Null(status.MinTileX);
                Assert.Equal(4, status.OccupiedCount);
                Assert.Equal(4, status.LoadedCount);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Open_TiledDirectoryOverLimit_LoadsWindowed()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);

                WindowStatusResult status = session.WindowStatus();
                Assert.True(status.Tiled);
                Assert.True(status.Windowed);
                Assert.Equal(4, status.OccupiedCount);
                Assert.True(status.LoadedCount < status.OccupiedCount);
                Assert.NotNull(status.MinTileX);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void SetWindow_ThrowsOnMonolithicDocument()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
                var session = new MapEditSession();
                session.Open(path);

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    session.SetWindow(-10f, -10f, 10f, 10f));
                Assert.Contains("tiled", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SetWindow_ThrowsWhenDirtyWithoutDiscard()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession();
                session.Open(dir);

                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "new-1", Kind = "rock", X = 1f, Z = 1f });
                    return 0;
                }, worldChanged: false);
                Assert.True(session.IsDirty);

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    session.SetWindow(-100f, -100f, 100f, 100f));
                Assert.Contains("unsaved changes", ex.Message);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void SetWindow_DiscardMovesWindowAndClearsUnsavedState()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);   // windowed to tile (0, 0) only

                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "temp", Kind = "rock", X = 1f, Z = 1f });
                    return 0;
                }, worldChanged: false);
                Assert.True(session.IsDirty);

                // Move the window to tile (-2, 0), discarding the unsaved add: this session keeps no undo
                // stack across calls, so the prior in-memory state simply is not there any more after reload.
                WindowStatusResult status = session.SetWindow(-1024f, 0f, -513f, 511f, discard: true);

                Assert.False(session.IsDirty);
                Assert.True(status.Windowed);
                Assert.Equal(-2, status.MinTileX);
                Assert.Equal(0, status.MinTileZ);
                bool stillThere = session.WithDocument((d, r) => d.Placements.Any(p => p.Id == "temp"));
                Assert.False(stillThere);
                bool hasMovedInContent = session.WithDocument((d, r) => d.Placements.Any(p => p.Id == "p-c"));
                Assert.True(hasMovedInContent);   // the new window's own content loaded correctly
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Save_ThrowsWhenAnItemMovedIntoAnUnloadedTile()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);   // windowed: only tile (0, 0) loaded

                // p-a is loaded (tile (0, 0)). Move it into tile (-2, 0), which the index marks occupied but
                // this window never loaded.
                session.Mutate((d, r) =>
                {
                    MapPlacement p = d.Placements.Single(pl => pl.Id == "p-a");
                    p.X = -600f;
                    return 0;
                }, worldChanged: false);

                MapDocumentException ex = Assert.Throws<MapDocumentException>(() => session.Save());
                Assert.Contains("p-a", ex.Message);
                Assert.Contains("set_window", ex.Message);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void ConvertToTiled_PreservesTileSizeAndWorldHash()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocument original = SampleDocs.SampleDoc();
                MapDocumentFile.Save(original, path);
                string expectedHash = MapDocumentHash.OfWorld(MapDocumentFile.Load(path));

                var session = new MapEditSession();
                session.Open(path);

                string tiledDir = Path.Combine(dir, "tiled");
                ConvertResult result = session.ConvertToTiled(tiledDir);

                Assert.Equal(tiledDir, result.Path);
                Assert.Equal("Tiled", result.Form);
                Assert.Equal(original.TileSize, result.TileSize);
                Assert.Equal(expectedHash, result.WorldHash);
                Assert.True(File.Exists(Path.Combine(tiledDir, "map.json")));
                Assert.Equal(tiledDir, session.DocumentPath);

                MapDocument reloaded = MapDocumentFile.LoadTiled(tiledDir);
                Assert.Equal(expectedHash, MapDocumentHash.OfWorld(reloaded));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ConvertToSingle_PreservesTileSizeAndWorldHash()
        {
            string tiledDir = TiledFixture.NewDirectory();
            string outDir = NewTempDir();
            try
            {
                MapDocument doc = TiledFixture.SampleDoc();
                MapDocumentFile.SaveTiled(doc, tiledDir);
                string expectedHash = MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(tiledDir));

                var session = new MapEditSession();
                session.Open(tiledDir);   // whole load, under the default limit

                string singlePath = Path.Combine(outDir, "converted.map.json");
                ConvertResult result = session.ConvertToSingle(singlePath);

                Assert.Equal("Monolithic", result.Form);
                Assert.Equal(doc.TileSize, result.TileSize);
                Assert.Equal(expectedHash, result.WorldHash);
                Assert.True(File.Exists(singlePath));

                MapDocument reloaded = MapDocumentFile.Load(singlePath);
                Assert.Equal(expectedHash, MapDocumentHash.OfWorld(reloaded));
            }
            finally { TiledFixture.Delete(tiledDir); Directory.Delete(outDir, recursive: true); }
        }

        [Fact]
        public void ConvertToTiled_RefusesAWindowedDocument()
        {
            string dir = TiledFixture.NewDirectory();
            string outDir = NewTempDir();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);

                string target = Path.Combine(outDir, "copy");
                Assert.Throws<MapDocumentException>(() => session.ConvertToTiled(target));
            }
            finally { TiledFixture.Delete(dir); Directory.Delete(outDir, recursive: true); }
        }

        /// <summary>Reproduces the exact data-loss path: two unrelated tiled worlds exist, and converting a
        /// document open in one session onto the OTHER world's directory used to call <c>SaveAs(..., Tiled)</c>
        /// with no existence check, silently overwriting world two's manifest with world one's and sweeping
        /// world two's tiles as orphans. The fix refuses whenever the target already holds a tiled document,
        /// the same as <see cref="MapEditSession.ConvertToSingle"/> already refused an existing tiled
        /// target, and world two is left completely untouched.</summary>
        [Fact]
        public void ConvertToTiled_RefusesAnExistingTiledDirectory()
        {
            string worldOneDir = TiledFixture.NewDirectory();
            string worldTwoDir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), worldOneDir);

                MapDocument worldTwo = TiledFixture.SampleDoc();
                worldTwo.Placements.Single(p => p.Id == "p-a").Yaw = 3f;   // distinguishes world two's content
                MapDocumentFile.SaveTiled(worldTwo, worldTwoDir);
                string worldTwoHashBefore = MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(worldTwoDir));
                var worldTwoFilesBefore = TiledFixture.TileFiles(worldTwoDir);

                var session = new MapEditSession();
                session.Open(worldOneDir);   // whole load, not windowed

                Assert.Throws<MapDocumentException>(() => session.ConvertToTiled(worldTwoDir));

                // World two is untouched: same content, same tile files on disk.
                Assert.Equal(worldTwoHashBefore, MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(worldTwoDir)));
                Assert.Equal(worldTwoFilesBefore, TiledFixture.TileFiles(worldTwoDir));
                Assert.Equal(3f, MapDocumentFile.LoadTiled(worldTwoDir).Placements.Single(p => p.Id == "p-a").Yaw);
            }
            finally { TiledFixture.Delete(worldOneDir); TiledFixture.Delete(worldTwoDir); }
        }

        [Fact]
        public void Retile_ChangesWorldHash_WarningStatesTheChange()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var session = new MapEditSession();
                session.Open(dir);   // whole load

                RetileResult result = session.Retile(256f);

                Assert.Equal(dir, result.Path);
                Assert.Equal(256f, result.TileSize);
                Assert.NotEqual(result.OldWorldHash, result.NewWorldHash);
                Assert.Contains("world hash changed", result.Warning);

                MapDocument reloaded = MapDocumentFile.LoadTiled(dir);
                Assert.Equal(256f, reloaded.TileSize);
                Assert.Equal(result.NewWorldHash, MapDocumentHash.OfWorld(reloaded));
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Retile_RefusesAWindowedDocument()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);

                Assert.Throws<MapDocumentException>(() => session.Retile(256f));
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Retile_RejectsNonPositiveOrNonFiniteTileSize()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
                var session = new MapEditSession();
                session.Open(path);

                Assert.Throws<ArgumentOutOfRangeException>(() => session.Retile(0f));
                Assert.Throws<ArgumentOutOfRangeException>(() => session.Retile(-5f));
                Assert.Throws<ArgumentOutOfRangeException>(() => session.Retile(float.NaN));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>A rejected retile must not leave the session broken: <c>Retile</c> used to assign
        /// <c>TileSize</c> before <c>SaveAuto</c> validates, so a save rejected for an unrelated reason
        /// (here, a duplicate placement id injected straight into the document) left the in-memory document
        /// reporting a tileSize that was never actually written. Confirms the size is restored, and that the
        /// session still saves correctly once the document is valid again.</summary>
        [Fact]
        public void Retile_RestoresTileSizeWhenTheSaveIsRejected()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var session = new MapEditSession();
                session.Open(dir);   // whole load, well under the default WholeWorldTileLimit
                float originalTileSize = session.WithDocument((d, r) => d.TileSize);

                // Bypass command guards: inject a duplicate placement id straight into the document, so the
                // structural validator inside Retile's SaveAuto call rejects the save for a reason that has
                // nothing to do with tileSize.
                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "p-a", Kind = "rock", X = 0f, Z = 0f });
                    return 0;
                }, worldChanged: false);

                Assert.Throws<MapDocumentException>(() => session.Retile(256f));

                // The rejected retile must not leave the in-memory tileSize ahead of what is on disk.
                Assert.Equal(originalTileSize, session.WithDocument((d, r) => d.TileSize));

                // The session is still healthy: fix the document and save now writes the ORIGINAL tileSize.
                session.Mutate((d, r) =>
                {
                    d.Placements.Remove(d.Placements.Last(p => p.Id == "p-a"));
                    return 0;
                }, worldChanged: false);
                session.Save();

                MapDocument reloaded = MapDocumentFile.LoadTiled(dir);
                Assert.Equal(originalTileSize, reloaded.TileSize);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void WindowStatus_ReportsTiledFalseForMonolithicDocument()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
                var session = new MapEditSession();
                session.Open(path);

                WindowStatusResult status = session.WindowStatus();
                Assert.False(status.Tiled);
                Assert.False(status.Windowed);
                Assert.Null(status.MinTileX);
                Assert.Equal(0, status.OccupiedCount);
                Assert.Equal(0, status.LoadedCount);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Validate_WindowedDocument_SkipsSchemaCheckGracefully()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession { WholeWorldTileLimit = 1, EditorWindowRadius = 0 };
                session.Open(dir);

                ValidateResult result = session.Validate();

                Assert.True(result.StructuralValid);
                Assert.False(result.SchemaChecked);   // skipped, not "checked and invalid"
                Assert.False(result.SchemaValid);
                Assert.Contains(result.SchemaErrors, e => e.Contains("windowed"));
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Create_RefusesAnExistingTiledDirectory()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
                var session = new MapEditSession();

                Assert.Throws<IOException>(() =>
                    session.Create(dir, "new-id", "New", -10f, -10f, 10f, 10f, overwrite: false));
            }
            finally { TiledFixture.Delete(dir); }
        }
    }
}
