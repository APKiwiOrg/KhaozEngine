using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Format version 3, the derived schemas, the hash-scheme rules, and the per-tile validation
    /// subset a tile read runs in place of the whole-document validator it cannot use.</summary>
    public class MapTiledFormatTests
    {
        static readonly MapTileRect OriginWindow = new(new MapTileCoord(0, 0), new MapTileCoord(0, 0));

        /// <summary>A genuine v2 document: the current writer's output with tileSize removed and the version
        /// wound back, so the migration has something real to stamp.</summary>
        static string V2Json()
        {
            JsonNode root = JsonNode.Parse(MapDocumentFile.SaveText(TiledDocFixture.SampleDoc()))!;
            root.AsObject().Remove("tileSize");
            root["formatVersion"] = 2;
            return root.ToJsonString();
        }

        [Fact]
        public void V2Document_LoadsAtV3WithDefaultTileSize()
        {
            MapDocument back = MapDocumentFile.LoadText(V2Json());
            Assert.Equal(3, MapDocumentFile.CurrentFormatVersion);
            Assert.Equal(MapDocumentFile.CurrentFormatVersion, back.FormatVersion);
            Assert.Equal(MapDocumentFile.DefaultTileSize, back.TileSize);
        }

        [Fact]
        public void V2Migration_StampsTheDefaultEvenOverAStrayValue()
        {
            // The step stamps the default and nothing else: any default is as arbitrary as any other for a
            // document that had no tile concept, so the rule is "deterministic and documented".
            JsonNode root = JsonNode.Parse(V2Json())!;
            root["tileSize"] = 999f;
            Assert.Equal(MapDocumentFile.DefaultTileSize, MapDocumentFile.LoadText(root.ToJsonString()).TileSize);
        }

        [Fact]
        public void V1Document_StillLoadsThroughTheChain()
        {
            JsonNode root = JsonNode.Parse(V2Json())!;
            root.AsObject().Remove("terrainOverrides");
            root["formatVersion"] = 1;

            MapDocument back = MapDocumentFile.LoadText(root.ToJsonString());
            Assert.Equal(MapDocumentFile.CurrentFormatVersion, back.FormatVersion);
            Assert.Null(back.TerrainOverrides);
            Assert.Equal(MapDocumentFile.DefaultTileSize, back.TileSize);
        }

        [Fact]
        public void NewerThanSupported_RejectedByTheVersionGate()
        {
            string json = MapDocumentFileTests.WithFormatVersion(
                MapDocumentFile.SaveText(TiledDocFixture.SampleDoc()), MapDocumentFile.CurrentFormatVersion + 1);
            var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json));
            Assert.Contains("newer than this engine supports", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TileSize_MustBePositiveAndAtLeastOneSculptTileWide()
        {
            var registry = MapDocRegistry.CreateDefault();

            MapDocument zero = TiledDocFixture.SampleDoc();
            zero.TileSize = 0f;
            Assert.Contains(MapDocumentValidator.Validate(zero, registry), e => e.Contains("tileSize", StringComparison.Ordinal));

            // The fixture's 2 m sculpt cells give a 64 m sculpt span, so a 32 m document tile cannot own them.
            MapDocument narrow = TiledDocFixture.SampleDoc();
            narrow.TileSize = 32f;
            Assert.Contains(MapDocumentValidator.Validate(narrow, registry), e => e.Contains("sculpt tile", StringComparison.Ordinal));

            MapDocument exact = TiledDocFixture.SampleDoc();
            exact.TileSize = 64f;
            Assert.Empty(MapDocumentValidator.Validate(exact, registry));
        }

        [Fact]
        public void WindowedLoad_RefusesOnSchemeVersionMismatch()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                SetSchemeVersion(directory, MapDocumentHash.SchemeVersion + 7);

                var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(directory, OriginWindow));
                Assert.Contains("hash scheme", ex.Message, StringComparison.Ordinal);

                // A WHOLE load at a mismatched scheme is fine: it can upgrade what it can read.
                MapDocument whole = MapDocumentFile.LoadTiled(directory);
                Assert.Equal(MapDocumentHash.SchemeVersion + 7, whole.Tiles!.SchemeVersion);
            });
        }

        [Fact]
        public void WholeLoadThenSave_UpgradesSchemeVersion()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                SetSchemeVersion(directory, MapDocumentHash.SchemeVersion + 7);

                MapDocument whole = MapDocumentFile.LoadTiled(directory);
                MapDocumentFile.SaveTiled(whole, directory);

                Assert.Equal(MapDocumentHash.SchemeVersion, MapDocumentFile.LoadTiled(directory).Tiles!.SchemeVersion);
                MapDocumentFile.LoadTiled(directory, OriginWindow);   // and windowing works again
            });
        }

        [Fact]
        public void SaveTiled_RefreshesTheDocumentsOwnIndex()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocument doc = TiledDocFixture.SampleDoc();
                Assert.Null(doc.Tiles);
                MapDocumentFile.SaveTiled(doc, directory);

                Assert.NotNull(doc.Tiles);
                Assert.False(doc.Tiles!.IsPartial);
                Assert.Equal(MapTiledFile.Normalize(directory), doc.Tiles.SourceDirectory);
                Assert.Equal(MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(directory)), MapDocumentHash.OfWorld(doc));
            });
        }

        [Theory]
        [InlineData("moved", "belongs to document tile")]
        [InlineData("duplicate", "duplicate placement id")]
        [InlineData("empty-id", "non-empty id")]
        [InlineData("short-deltas", "deltas")]
        [InlineData("foreign-sculpt", "is owned by document tile")]
        public void PerTileValidation_FailsLoudly(string breakage, string expected)
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                string relative = TiledDocFixture.TileFiles(directory)
                    .Single(f => f.Contains("t_0_0.", StringComparison.Ordinal));
                string path = Path.Combine(directory, relative);
                JsonNode tile = JsonNode.Parse(File.ReadAllText(path))!;
                JsonArray placements = tile["placements"]!.AsArray();

                switch (breakage)
                {
                    case "moved":
                        placements[0]!["x"] = -600f;   // now belongs to tile (-2, 0)
                        break;
                    case "duplicate":
                        placements[1]!["id"] = placements[0]!["id"]!.GetValue<string>();
                        break;
                    case "empty-id":
                        placements[0]!["id"] = "";
                        break;
                    case "short-deltas":
                        tile["sculpt"]![0]!["deltas"]!.AsArray().Clear();
                        break;
                    default:
                        tile["sculpt"]![0]!["tileX"] = 40;   // sculpt tile owned by a far-away document tile
                        break;
                }
                File.WriteAllText(path, tile.ToJsonString());

                var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(directory));
                Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
                Assert.Contains("(0, 0)", ex.Message, StringComparison.Ordinal);
                Assert.Contains(Path.GetFileName(relative), ex.Message, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void VerifyTiled_ReportsAnIdDuplicatedAcrossTiles()
        {
            // One tile cannot see another, so cross-tile uniqueness is a whole-load and VerifyTiled check.
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
                string path = Path.Combine(directory, TiledDocFixture.TileFiles(directory)
                    .Single(f => f.Contains("t_-2_0.", StringComparison.Ordinal)));
                JsonNode tile = JsonNode.Parse(File.ReadAllText(path))!;
                tile["placements"]![0]!["id"] = "p-a";
                File.WriteAllText(path, tile.ToJsonString());

                Assert.Contains(MapDocumentFile.VerifyTiled(directory),
                                e => e.Contains("duplicate placement id 'p-a'", StringComparison.Ordinal));
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(directory));
            });
        }

        [Fact]
        public void ManifestAndTileFiles_PassTheirDerivedSchemas()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);

                ValidationReport manifest = JsonSchemaValidator.Validate(
                    File.ReadAllText(Path.Combine(directory, "map.json")), MapDocumentSchema.GetManifestJson());
                Assert.True(manifest.IsValid, string.Join("\n", manifest.Errors));

                string tileSchema = MapDocumentSchema.GetTileJson();
                foreach (string file in TiledDocFixture.TileFiles(directory))
                {
                    ValidationReport report = JsonSchemaValidator.Validate(
                        File.ReadAllText(Path.Combine(directory, file)), tileSchema);
                    Assert.True(report.IsValid, file + "\n" + string.Join("\n", report.Errors));
                }
            });
        }

        [Fact]
        public void DerivedSchemas_AreClosedAndDropWhatMovedIntoTiles()
        {
            JsonObject manifest = JsonNode.Parse(MapDocumentSchema.GetManifestJson())!.AsObject();
            JsonObject properties = manifest["properties"]!.AsObject();
            Assert.True(properties.ContainsKey("schemeVersion"));
            Assert.True(properties.ContainsKey("sculptCellSize"));
            Assert.True(properties.ContainsKey("tiles"));
            foreach (string moved in new[] { "placements", "spawns", "playerSpawns", "terrainOverrides" })
                Assert.False(properties.ContainsKey(moved), $"{moved} belongs in a tile file, not the manifest.");
            Assert.False(manifest["additionalProperties"]!.GetValue<bool>());

            // The tile schema still resolves the item shapes the document schema owns, so they cannot rot
            // apart: it carries the same $defs rather than a hand-copied duplicate.
            string tile = MapDocumentSchema.GetTileJson();
            Assert.Contains("#/$defs/placements", tile, StringComparison.Ordinal);
            Assert.Contains("#/$defs/sculptTiles", tile, StringComparison.Ordinal);

            JsonNode node = JsonNode.Parse("{\"placements\": [], \"unknownField\": 1}")!;
            Assert.False(JsonSchemaValidator.Validate(node.ToJsonString(), tile).IsValid);
        }

        [Fact]
        public void WriteAllTo_MaterializesEverySchemaUnderItsCanonicalName()
        {
            TiledDocFixture.InDirectory(directory =>
            {
                MapDocumentSchema.WriteAllTo(directory);
                foreach (string name in new[]
                         {
                             MapDocumentSchema.DocumentFileName,
                             MapDocumentSchema.ManifestFileName,
                             MapDocumentSchema.TileFileName,
                         })
                    Assert.True(File.Exists(Path.Combine(directory, name)), name);
            });
        }

        static void SetSchemeVersion(string directory, int scheme)
        {
            string path = Path.Combine(directory, "map.json");
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            root["schemeVersion"] = scheme;
            File.WriteAllText(path, root.ToJsonString());
        }
    }
}
