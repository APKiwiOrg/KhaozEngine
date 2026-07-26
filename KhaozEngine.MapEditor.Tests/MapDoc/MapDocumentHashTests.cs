using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>World identity: content-only per tile, order-independent, the same in both on-disk forms, and
    /// culture-proof. The last one is not defensive boilerplate: under ICU, sv-SE formats a negative integer
    /// with U+2212 MINUS SIGN, so a world with any negative tile coordinate would hash differently and write
    /// differently named files on a Swedish workstation, and a golden test running under the CI default
    /// culture cannot see any of it.</summary>
    public class MapDocumentHashTests
    {
        /// <summary>The digest of <see cref="TiledDocFixture.SampleDoc"/> under hash scheme 1. A change here
        /// is a canonicalization change and needs a <see cref="MapDocumentHash.SchemeVersion"/> bump, which
        /// invalidates every stored hash on purpose.</summary>
        const string GoldenDigest = "b9fae93cc3e51c9978e859ba425189fec9e721e462337fbf4274e06e547bf40a";

        [Fact]
        public void TileHash_IsOrderIndependent()
        {
            MapDocument forward = TiledDocFixture.SampleDoc();
            MapDocument reversed = TiledDocFixture.SampleDoc();
            reversed.Placements.Reverse();
            reversed.Spawns.Reverse();

            MapSpatialIndex a = MapSpatialIndex.Build(forward), b = MapSpatialIndex.Build(reversed);
            foreach (MapTileCoord tile in a.OccupiedTiles)
                Assert.Equal(MapDocumentHash.OfTile(a, tile), MapDocumentHash.OfTile(b, tile));
            Assert.Equal(MapDocumentHash.OfWorld(forward), MapDocumentHash.OfWorld(reversed));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WorldHash_MonolithicEqualsTiled(bool withSculpt)
        {
            MapDocument doc = withSculpt ? TiledDocFixture.SampleDoc() : TiledDocFixture.SampleDocWithoutSculpt();
            string monolithic = MapDocumentHash.OfWorld(doc);

            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                MapDocument tiled = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(monolithic, MapDocumentHash.OfWorld(tiled));
            });
        }

        [Fact]
        public void WorldHash_MonolithicEqualsTiled_ForAnEmptySculptBlockToo()
        {
            // "No sculpt" and "an empty sculpt block at the default cell size" are two on-disk states for the
            // same world, so they must hash identically or a round trip that changed nothing would fail.
            MapDocument nullBlock = TiledDocFixture.SampleDocWithoutSculpt();
            MapDocument emptyBlock = TiledDocFixture.SampleDocWithoutSculpt();
            emptyBlock.TerrainOverrides = new MapTerrainOverrides();
            Assert.Equal(MapDocumentHash.OfWorld(nullBlock), MapDocumentHash.OfWorld(emptyBlock));
        }

        [Fact]
        public void WorldHash_ChangesOnSculptDelta()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            string before = MapDocumentHash.OfWorld(doc);
            doc.TerrainOverrides!.AddDelta(4, 6, 0.001f);
            Assert.NotEqual(before, MapDocumentHash.OfWorld(doc));
        }

        [Fact]
        public void WorldHash_ChangesOnPlacementMove()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            string before = MapDocumentHash.OfWorld(doc);
            doc.Placements[0].X += 0.5f;
            Assert.NotEqual(before, MapDocumentHash.OfWorld(doc));
        }

        [Fact]
        public void WorldHash_ChangesOnBoundsChange()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            string before = MapDocumentHash.OfWorld(doc);
            doc.Bounds.MaxX += 1f;
            Assert.NotEqual(before, MapDocumentHash.OfWorld(doc));
        }

        [Fact]
        public void WorldHash_ChangesOnRetile()
        {
            // Re-tiling moves no content at all, and still changes world identity, because the alternative is
            // a hash that cannot certify the bucketing the tile hashes were computed under.
            MapDocument doc = TiledDocFixture.SampleDoc();
            string before = MapDocumentHash.OfWorld(doc);
            doc.TileSize = 256f;
            Assert.NotEqual(before, MapDocumentHash.OfWorld(doc));
        }

        [Fact]
        public void ConvertRoundTrip_PreservesTileSize()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            doc.TileSize = 256f;
            string expected = MapDocumentHash.OfWorld(doc);

            TiledDocFixture.InDirectory(root =>
            {
                string tiled = Path.Combine(root, "island.map");
                string single = Path.Combine(root, "island.map.json");

                MapDocumentFile.SaveAs(doc, tiled, MapDocumentForm.Tiled);
                MapDocument fromTiled = MapDocumentFile.LoadTiled(tiled);
                Assert.Equal(256f, fromTiled.TileSize);

                MapDocumentFile.SaveAs(fromTiled, single, MapDocumentForm.Monolithic);
                MapDocument back = MapDocumentFile.Load(single);
                Assert.Equal(256f, back.TileSize);
                Assert.Equal(expected, MapDocumentHash.OfWorld(back));
            });
        }

        [Fact]
        public void MonolithicRoundTripThroughTiled_DropsTheEmptySculptBlock()
        {
            // The empty-block normalization: a null sculpt block becomes an empty block at the default cell
            // size on the way back out of the tiled form, and the monolithic writer collapses that back to no
            // block, so the round trip does not grow a terrainOverrides key out of nothing.
            //
            // Note what is NOT claimed: full byte equality. The tiled form stores the four bucketed lists in
            // canonical order (ascending tile, then ordinal id), so a document authored in some other order
            // comes back reordered. That is a storage-order change with no semantic effect and no hash
            // effect, and it is the price of tile files that are deterministic for git.
            MapDocument doc = TiledDocFixture.SampleDocWithoutSculpt();
            string first = MapDocumentFile.SaveText(doc);
            Assert.DoesNotContain("terrainOverrides", first, StringComparison.Ordinal);

            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                MapDocument back = MapDocumentFile.LoadTiled(directory);
                Assert.DoesNotContain("terrainOverrides", MapDocumentFile.SaveText(back), StringComparison.Ordinal);
                Assert.Equal(TiledDocFixture.PlacementKeys(doc), TiledDocFixture.PlacementKeys(back));
                Assert.Equal(MapDocumentHash.OfWorld(doc), MapDocumentHash.OfWorld(back));
            });
        }

        [Fact]
        public void EmptySculptBlockAtANonDefaultCellSize_SurvivesAMonolithicSave()
        {
            // The narrowing that keeps the normalization from eating authored information: only an empty
            // block at the DEFAULT cell size collapses, because that one carries nothing.
            MapDocument doc = TiledDocFixture.SampleDocWithoutSculpt();
            doc.TerrainOverrides = new MapTerrainOverrides(2f);
            MapDocument back = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
            Assert.NotNull(back.TerrainOverrides);
            Assert.Equal(2f, back.TerrainOverrides!.CellSize);
        }

        [Fact]
        public void WorldHash_UnchangedOnDisplayNameChange()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            string before = MapDocumentHash.OfWorld(doc);
            doc.DisplayName = "Something Else Entirely";
            doc.Schema = "some-schema.json";
            Assert.Equal(before, MapDocumentHash.OfWorld(doc));
        }

        [Fact]
        public void WorldHash_ReadsFromManifestIndex()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            string expected = MapDocumentHash.OfWorld(doc);

            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                using MapDocumentSource source = MapDocumentSource.OpenTiled(directory);

                // Every tile file gone: if OfWorld opened one it could not answer at all.
                Directory.Delete(Path.Combine(directory, "tiles"), recursive: true);
                Assert.Equal(expected, MapDocumentHash.OfWorld(source.Manifest));
            });
        }

        [Fact]
        public void Compose_ThrowsOnUnorderedEntries()
        {
            var ordered = new List<MapTileEntry>
            {
                new(new MapTileCoord(0, 0), "a", true),
                new(new MapTileCoord(1, 0), "b", true),
                new(new MapTileCoord(0, 1), "c", true),
            };
            Assert.NotNull(MapDocumentHash.Compose("manifest", ordered));

            var swapped = new List<MapTileEntry> { ordered[1], ordered[0], ordered[2] };
            Assert.Throws<MapDocumentException>(() => MapDocumentHash.Compose("manifest", swapped));

            var duplicated = new List<MapTileEntry> { ordered[0], ordered[0] };
            Assert.Throws<MapDocumentException>(() => MapDocumentHash.Compose("manifest", duplicated));
        }

        [Fact]
        public void WorldHash_MatchesGoldenDigest()
        {
            Assert.Equal(GoldenDigest, MapDocumentHash.OfWorld(TiledDocFixture.SampleDoc()));
        }

        [Fact]
        public void WorldHash_MatchesGoldenDigest_UnderSwedishCulture()
        {
            CultureInfo swedish;
            try { swedish = CultureInfo.GetCultureInfo("sv-SE"); }
            catch (CultureNotFoundException) { return; }   // globalization-invariant runtime: nothing to prove

            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = swedish;

                // The precondition this test exists for: sv-SE really does format a negative int differently.
                Assert.NotEqual("-3", (-3).ToString(CultureInfo.CurrentCulture));

                Assert.Equal(GoldenDigest, MapDocumentHash.OfWorld(TiledDocFixture.SampleDoc()));

                string[] swedishNames = SavedTileFiles();
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                Assert.Equal(SavedTileFiles(), swedishNames);
                Assert.Contains(swedishNames, n => n.Contains("t_-1_0.", StringComparison.Ordinal));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        static string[] SavedTileFiles()
        {
            string[] names = Array.Empty<string>();
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                names = TiledDocFixture.TileFiles(directory).Select(f => f.Replace(Path.DirectorySeparatorChar, '/')).ToArray();
            });
            return names;
        }
    }
}
