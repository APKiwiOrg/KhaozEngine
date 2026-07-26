using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The tiled form end to end: a tiled load reproduces a whole load exactly, the form comes from
    /// the path or the caller and never from an extension, and every save entry point refuses a windowed
    /// document.</summary>
    public class MapTiledFileTests
    {
        static readonly MapTileRect OriginWindow = new(new MapTileCoord(0, 0), new MapTileCoord(0, 0));

        [Fact]
        public void TiledLoad_EqualsWholeLoad()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            var registry = MapDocRegistry.CreateDefault();

            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory, registry);
                MapDocument tiled = MapDocumentFile.LoadTiled(directory, new MapDocumentLoadOptions { Registry = registry });

                Assert.Equal(TiledDocFixture.PlacementKeys(doc), TiledDocFixture.PlacementKeys(tiled));
                Assert.Equal(TiledDocFixture.SpawnKeys(doc), TiledDocFixture.SpawnKeys(tiled));
                Assert.Equal(TiledDocFixture.PlayerSpawnKeys(doc), TiledDocFixture.PlayerSpawnKeys(tiled));
                Assert.Equal(TiledDocFixture.SculptKeys(doc), TiledDocFixture.SculptKeys(tiled));
                Assert.Equal(doc.TileSize, tiled.TileSize);
                Assert.Equal(doc.TerrainOverrides!.CellSize, tiled.TerrainOverrides!.CellSize);

                TerrainField a = MapRuntime.BuildField(doc, registry);
                TerrainField b = MapRuntime.BuildField(tiled, registry);
                for (float x = -700f; x <= 700f; x += 37f)
                    for (float z = -700f; z <= 700f; z += 41f)
                        Assert.Equal(a.SampleHeight(x, z), b.SampleHeight(x, z));

                Assert.Equal(MapRuntime.BuildPlacements(doc, a).Select(Key).OrderBy(k => k, StringComparer.Ordinal),
                             MapRuntime.BuildPlacements(tiled, b).Select(Key).OrderBy(k => k, StringComparer.Ordinal));
            });
        }

        [Fact]
        public void TiledPartialLoads_UnionEqualsWhole()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                MapDocument whole = MapDocumentFile.LoadTiled(directory);

                var placements = new List<string>();
                var sculpt = new List<string>();
                foreach (MapTileEntry entry in whole.Tiles!.Entries)
                {
                    var window = new MapTileRect(entry.Coord, entry.Coord);
                    MapDocument part = MapDocumentFile.LoadTiled(directory, window);
                    Assert.True(part.Tiles!.IsPartial || whole.Tiles.Entries.Count == 1);
                    Assert.Equal(1, part.Tiles.LoadedCount);
                    placements.AddRange(TiledDocFixture.PlacementKeys(part));
                    sculpt.AddRange(TiledDocFixture.SculptKeys(part));
                }

                Assert.Equal(TiledDocFixture.PlacementKeys(whole), placements.OrderBy(k => k, StringComparer.Ordinal));
                Assert.Equal(TiledDocFixture.SculptKeys(whole), sculpt.OrderBy(k => k, StringComparer.Ordinal));
            });
        }

        [Fact]
        public void DetectForm_ReadsThePathAndNeverTheExtension()
        {
            TiledDocFixture.InDirectory(root =>
            {
                string missing = Path.Combine(root, "nope.map.json");
                Assert.Equal(MapDocumentForm.None, MapDocumentFile.DetectForm(missing));

                string file = Path.Combine(root, "island.map.json");
                MapDocumentFile.Save(TiledDocFixture.SampleDoc(), file);
                Assert.Equal(MapDocumentForm.Monolithic, MapDocumentFile.DetectForm(file));

                // "island.map" has extension ".map", not none, which is exactly the input an extension
                // heuristic routes to a file write.
                string directory = Path.Combine(root, "island.map");
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                Assert.Equal(MapDocumentForm.Tiled, MapDocumentFile.DetectForm(directory));
                Assert.True(File.Exists(Path.Combine(directory, "map.json")));
            });
        }

        [Fact]
        public void Load_DispatchesOnTheForm()
        {
            TiledDocFixture.InDirectory(root =>
            {
                string directory = Path.Combine(root, "island.map");
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                MapDocument back = MapDocumentFile.Load(directory);
                Assert.NotNull(back.Tiles);
                Assert.Equal(4, back.Tiles!.Entries.Count);
            });
        }

        [Fact]
        public void LoadTiled_OnADirectoryWithNoManifest_FailsLoudly()
        {
            TiledDocFixture.InDirectory(root =>
            {
                var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(root));
                Assert.Contains("map.json", ex.Message, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void SaveAuto_ThrowsForANonExistentPath()
        {
            TiledDocFixture.InDirectory(root =>
            {
                string missing = Path.Combine(root, "brand-new.map");
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveAuto(TiledDocFixture.SampleDoc(), missing));
                Assert.False(File.Exists(missing));
                Assert.False(Directory.Exists(missing));
            });
        }

        [Fact]
        public void SaveAuto_SavesBackInTheFormItOpened()
        {
            TiledDocFixture.InDirectory(root =>
            {
                string directory = Path.Combine(root, "island.map");
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                MapDocument loaded = MapDocumentFile.LoadTiled(directory);
                loaded.Placements[0].Yaw = 2.5f;
                MapDocumentFile.SaveAuto(loaded, directory);
                Assert.Equal(MapDocumentForm.Tiled, MapDocumentFile.DetectForm(directory));

                string file = Path.Combine(root, "island.map.json");
                MapDocumentFile.Save(TiledDocFixture.SampleDoc(), file);
                MapDocumentFile.SaveAuto(MapDocumentFile.Load(file), file);
                Assert.Equal(MapDocumentForm.Monolithic, MapDocumentFile.DetectForm(file));
            });
        }

        [Fact]
        public void SaveAs_WritesTheNamedFormRegardlessOfExtension()
        {
            TiledDocFixture.InDirectory(root =>
            {
                // Named tiled at a ".map" path: a directory, not a file.
                string tiled = Path.Combine(root, "island.map");
                MapDocumentFile.SaveAs(TiledDocFixture.SampleDoc(), tiled, MapDocumentForm.Tiled);
                Assert.True(Directory.Exists(tiled));

                // Named monolithic at an extension-less path: a file, not a directory.
                string single = Path.Combine(root, "island");
                MapDocumentFile.SaveAs(TiledDocFixture.SampleDoc(), single, MapDocumentForm.Monolithic);
                Assert.True(File.Exists(single));

                Assert.Throws<MapDocumentException>(
                    () => MapDocumentFile.SaveAs(TiledDocFixture.SampleDoc(), Path.Combine(root, "x"), MapDocumentForm.None));
            });
        }

        [Fact]
        public void SaveTiled_RefusesToWriteANullIndexOverATiledDirectory()
        {
            // The belt behind the editor's open gate: even if something hands the writer a blank document,
            // the sweep never gets the chance to delete a world the document does not know about.
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                IReadOnlyList<string> before = TiledDocFixture.TileFiles(directory);

                var blank = new MapDocument { Id = "untitled", Bounds = new MapBounds { MinX = -1f, MinZ = -1f, MaxX = 1f, MaxZ = 1f } };
                Assert.Null(blank.Tiles);
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(blank, directory));
                Assert.Equal(before, TiledDocFixture.TileFiles(directory));
            });
        }

        [Theory]
        [InlineData("Save")]
        [InlineData("SaveText")]
        [InlineData("SaveTo")]
        [InlineData("SaveAuto")]
        [InlineData("SaveAs")]
        public void PartialDocument_RefusedByEverySaveEntryPoint(string entryPoint)
        {
            TiledDocFixture.InDirectory(root =>
            {
                string directory = Path.Combine(root, "island.map");
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                MapDocument window = MapDocumentFile.LoadTiled(directory, OriginWindow);
                Assert.True(window.Tiles!.IsPartial);

                string file = Path.Combine(root, "whole.map.json");
                Action save = entryPoint switch
                {
                    "Save" => () => MapDocumentFile.Save(window, file),
                    "SaveText" => () => MapDocumentFile.SaveText(window),
                    "SaveTo" => () => MapDocumentFile.SaveTo(window, Stream.Null),
                    "SaveAuto" => () => MapDocumentFile.SaveAuto(window, directory + "-copy"),
                    _ => () => MapDocumentFile.SaveAs(window, file, MapDocumentForm.Monolithic),
                };

                if (entryPoint == "SaveAuto") Directory.CreateDirectory(directory + "-copy");
                Assert.Throws<MapDocumentException>(save);
                Assert.False(File.Exists(file));
            });
        }

        [Fact]
        public void WindowedLoad_CarriesUnloadedEntriesVerbatim()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                MapDocument whole = MapDocumentFile.LoadTiled(directory);
                MapDocument window = MapDocumentFile.LoadTiled(directory, OriginWindow);

                Assert.True(window.Tiles!.IsPartial);
                Assert.Equal(1, window.Tiles.LoadedCount);
                Assert.Equal(whole.Tiles!.Entries.Count, window.Tiles.Entries.Count);
                Assert.Equal(MapTiledFile.Normalize(directory), window.Tiles.SourceDirectory);

                // Every entry's hash is carried through untouched, loaded or not.
                foreach (MapTileEntry entry in whole.Tiles.Entries)
                {
                    Assert.True(window.Tiles.TryGet(entry.Coord, out MapTileEntry mine));
                    Assert.Equal(entry.Hash, mine.Hash);
                    Assert.Equal(entry.Coord == new MapTileCoord(0, 0), mine.Loaded);
                }

                // Only the loaded tile's content is present in the document itself.
                Assert.Equal(new[] { "p-a", "p-b" }, window.Placements.Select(p => p.Id).OrderBy(i => i, StringComparer.Ordinal));

                // Saving the window back preserves everything the window never loaded.
                window.Placements.Single(p => p.Id == "p-a").Yaw = 2.75f;
                MapDocumentFile.SaveTiled(window, directory);

                MapDocument reloaded = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(2.75f, reloaded.Placements.Single(p => p.Id == "p-a").Yaw);
                Assert.Equal(TiledDocFixture.SpawnKeys(whole), TiledDocFixture.SpawnKeys(reloaded));
                Assert.Equal(TiledDocFixture.SculptKeys(whole), TiledDocFixture.SculptKeys(reloaded));
                Assert.Equal("hut", reloaded.Placements.Single(p => p.Id == "p-c").Kind);
            });
        }

        [Fact]
        public void SaveTiled_ThrowsWhenAnItemMovedIntoAnUnloadedTile()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                MapDocument window = MapDocumentFile.LoadTiled(directory, OriginWindow);

                // p-c and s-b live in tile (-2, 0), which this window never loaded. Writing that tile would
                // replace its real content with just the moved item.
                window.Placements.Single(p => p.Id == "p-a").X = -600f;

                var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, directory));
                Assert.Contains("p-a", ex.Message, StringComparison.Ordinal);
                Assert.Contains("(-2, 0)", ex.Message, StringComparison.Ordinal);
                Assert.Contains("set_window", ex.Message, StringComparison.Ordinal);

                // And nothing was written: the guard runs before any file is touched.
                Assert.Equal("hut", MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-c").Kind);
            });
        }

        [Fact]
        public void PartialDocument_RefusesToWriteToADifferentDirectory()
        {
            TiledDocFixture.InDirectory(root =>
            {
                string directory = Path.Combine(root, "island.map");
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                MapDocument window = MapDocumentFile.LoadTiled(directory, OriginWindow);

                string elsewhere = Path.Combine(root, "copy.map");
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(window, elsewhere));

                // The same window back to its own directory is fine.
                MapDocumentFile.SaveTiled(window, directory);
            });
        }

        [Fact]
        public void MapDocumentSource_ReadsOneTileAtATimeAndRefusesAnUnindexedOne()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                using MapDocumentSource source = MapDocumentSource.OpenTiled(directory);

                Assert.Empty(source.Manifest.Placements);
                Assert.Equal(0, source.Tiles.LoadedCount);
                Assert.Equal(MapDocumentHash.SchemeVersion, source.Tiles.SchemeVersion);

                MapTileContent origin = source.ReadTile(new MapTileCoord(0, 0));
                Assert.Equal(new[] { "p-a", "p-b" }, origin.Placements.Select(p => p.Id).OrderBy(i => i, StringComparer.Ordinal));
                Assert.Single(origin.SculptTiles);

                Assert.Throws<MapDocumentException>(() => source.ReadTile(new MapTileCoord(42, 42)));
            });
        }

        [Fact]
        public void MapDocumentSource_FromDocument_IndexesAWholeInMemoryWorld()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            using MapDocumentSource source = MapDocumentSource.FromDocument(doc);

            Assert.Equal(4, source.Tiles.Entries.Count);
            Assert.False(source.Tiles.IsPartial);
            Assert.Null(source.Tiles.SourceDirectory);
            Assert.Equal(MapDocumentHash.OfWorld(doc), MapDocumentHash.OfWorld(source.Manifest));

            MapTileContent tile = source.ReadTile(new MapTileCoord(1, -2));
            Assert.Equal("p-d", Assert.Single(tile.Placements).Id);
        }

        static string Key(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Y:F4}|{p.Z:F4}|{p.Scale:F4}|{p.Yaw:F4}|{p.Variant}";
    }
}
