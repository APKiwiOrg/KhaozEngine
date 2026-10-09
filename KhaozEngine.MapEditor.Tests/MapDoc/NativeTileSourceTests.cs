using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>A native document serves the same tile content from memory as from its tiled directory, so a
    /// placement keeps its native identity whichever storage form a consumer streams it from.</summary>
    public class NativeTileSourceTests
    {
        const long AboveDoublePrecision = (1L << 53) + 1;

        static MapDocument NativeSampleDoc()
        {
            MapDocument doc = TiledDocFixture.SampleDoc();
            doc.ResolverIdentity = new MapResolverIdentityDoc(1, 1);
            for (int i = 0; i < doc.Placements.Count; i++)
            {
                MapPlacement p = doc.Placements[i];
                p.NumericId = AboveDoublePrecision + i;
                p.AssetId = "assets/" + p.Kind + "-variant";
                p.DisplayName = "Placement " + p.Id;
                p.Tags.Add("tag-" + p.Id);
            }
            doc.NumericIdHighWaterMark = AboveDoublePrecision + doc.Placements.Count;
            return doc;
        }

        static string Key(MapPlacement p) => JsonSerializer.Serialize(p);

        static List<MapPlacement> AllPlacements(MapDocumentSource source) =>
            source.Tiles.Entries.SelectMany(e => source.ReadTile(e.Coord).Placements).OrderBy(p => p.Id).ToList();

        [Fact]
        public void FromDocument_ReadTile_MatchesOpenTiledForNativePlacementFields()
        {
            MapDocument doc = NativeSampleDoc();
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(doc, directory);
                using MapDocumentSource disk = MapDocumentSource.OpenTiled(directory);
                using MapDocumentSource memory = MapDocumentSource.FromDocument(doc);

                Assert.Equal(disk.Tiles.Entries.Select(e => (e.Coord, e.Hash)), memory.Tiles.Entries.Select(e => (e.Coord, e.Hash)));
                foreach (MapTileEntry entry in disk.Tiles.Entries)
                    Assert.Equal(disk.ReadTile(entry.Coord).Placements.Select(Key).OrderBy(k => k),
                                 memory.ReadTile(entry.Coord).Placements.Select(Key).OrderBy(k => k));

                MapPlacement first = AllPlacements(memory)[0];
                Assert.Equal("p-a", first.Id);
                Assert.Equal(AboveDoublePrecision, first.NumericId);
                Assert.Equal("assets/rock-variant", first.AssetId);
                Assert.NotEqual(first.Kind, first.AssetId);
                Assert.Equal("Placement p-a", first.DisplayName);
            });
        }

        /// <summary>Served content is a copy. The source's buckets still hold the live document objects, so only
        /// a placement already handed out is frozen against later edits of the original.</summary>
        [Fact]
        public void FromDocument_ReadTile_ServedPlacementIsDetachedFromTheSourceDocument()
        {
            MapDocument doc = NativeSampleDoc();
            using MapDocumentSource memory = MapDocumentSource.FromDocument(doc);
            MapPlacement original = doc.Placements.Single(p => p.Id == "p-a");
            MapPlacement served = AllPlacements(memory)[0];

            Assert.NotSame(original, served);
            Assert.NotSame(original.Tags, served.Tags);
            original.NumericId = 1;
            original.AssetId = "changed";
            original.DisplayName = "changed";
            original.Tags.Add("changed");

            Assert.Equal(AboveDoublePrecision, served.NumericId);
            Assert.Equal("assets/rock-variant", served.AssetId);
            Assert.Equal("Placement p-a", served.DisplayName);
            Assert.DoesNotContain("changed", served.Tags);
        }

        /// <summary>The manifest is a metadata snapshot. A terrain edit published into the document afterward
        /// does not show through the source's surfaces.</summary>
        [Fact]
        public void FromDocument_ManifestSurfacesIgnoreALaterPublishedTerrainEdit()
        {
            EditorDocument editor = TransactionFixtures.NativeWithSurfaces();
            using MapDocumentSource source = MapDocumentSource.FromDocument(editor.Doc);
            MapSurfaceRef[] refs = source.Manifest.Surfaces.Refs.ToArray();
            MapPatchKey[] patches = source.Manifest.Surfaces.Patches.Keys.ToArray();

            editor.Execute(new TerrainEditCommand(new MapReplaceTopology(Array.Empty<MapSurfacePatch>(),
                new MapPatchKey[] { new("ground", 0, 0), new("ground", 1, 0) }, Array.Empty<MapSurfaceRef>(), new[] { "ground" })));

            Assert.DoesNotContain(editor.Doc.Surfaces.Refs, s => s.Id == "ground");
            Assert.Equal(new[] { "ground", "ridge", "far" }, source.Manifest.Surfaces.Refs.Select(s => s.Id));
            Assert.Equal(refs, source.Manifest.Surfaces.Refs);
            Assert.Equal(patches, source.Manifest.Surfaces.Patches.Keys);
        }
    }
}
