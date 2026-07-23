using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The format-v2 terrainOverrides sculpt layer: v1 migration identity, save/load stability, the
    /// tile authoring API, the out-of-bounds writer refusal, and deterministic composition through
    /// <see cref="MapRuntime.BuildField"/>.</summary>
    public class TerrainSculptFormatTests
    {
        static MapDocument MinimalDoc(float min, float max) => new()
        {
            Id = "sculpt-zone",
            Bounds = new MapBounds { MinX = min, MinZ = min, MaxX = max, MaxZ = max },
        };

        [Fact]
        public void V1_migrates_to_v2_with_no_overrides_and_identical_terrain()
        {
            MapDocument doc = MapDocumentFileTests.SampleDoc();
            string v2json = MapDocumentFile.SaveText(doc);
            string v1json = v2json.Replace("\"formatVersion\": 2", "\"formatVersion\": 1");

            MapDocument fromV2 = MapDocumentFile.LoadText(v2json);
            MapDocument fromV1 = MapDocumentFile.LoadText(v1json);   // runs the built-in 1 -> 2 migration

            Assert.Equal(2, fromV1.FormatVersion);
            Assert.Null(fromV1.TerrainOverrides);

            var registry = MapDocRegistry.CreateDefault();
            var f2 = MapRuntime.BuildField(fromV2, registry);
            var f1 = MapRuntime.BuildField(fromV1, registry);
            for (float x = -60f; x <= 60f; x += 12f)
            for (float z = -60f; z <= 60f; z += 12f)
                Assert.Equal(f2.SampleHeight(x, z), f1.SampleHeight(x, z));
        }

        [Fact]
        public void Sculpt_block_survives_save_load_and_serializes_stably()
        {
            MapDocument doc = MapDocumentFileTests.SampleDoc();
            var overrides = new MapTerrainOverrides(0.25f);
            overrides.SetDelta(5, 7, 3f);
            overrides.SetDelta(6, 7, -1.5f);
            overrides.SetDelta(-3, 2, 2f);   // negative-coordinate tile
            doc.TerrainOverrides = overrides;

            string json = MapDocumentFile.SaveText(doc);
            MapDocument back = MapDocumentFile.LoadText(json);

            Assert.NotNull(back.TerrainOverrides);
            Assert.Equal(0.25f, back.TerrainOverrides!.CellSize);
            Assert.Equal(overrides.TileCount, back.TerrainOverrides.TileCount);
            Assert.Equal(3f, back.TerrainOverrides.GetDelta(5, 7));
            Assert.Equal(-1.5f, back.TerrainOverrides.GetDelta(6, 7));
            Assert.Equal(2f, back.TerrainOverrides.GetDelta(-3, 2));

            // Re-serializing the reloaded document produces byte-identical JSON (deterministic tile order).
            Assert.Equal(json, MapDocumentFile.SaveText(back));
        }

        [Fact]
        public void Tile_authoring_api_sets_adds_reads_and_counts_tiles()
        {
            var o = new MapTerrainOverrides();
            Assert.True(o.IsEmpty);
            Assert.Equal(MapTerrainOverrides.DefaultCellSize, o.CellSize);
            Assert.Equal(0, o.TileCount);

            o.SetDelta(5, 7, 3f);
            Assert.False(o.IsEmpty);
            Assert.Equal(1, o.TileCount);
            Assert.Equal(3f, o.GetDelta(5, 7));
            Assert.Equal(0f, o.GetDelta(6, 7));   // unset cell reads 0

            o.AddDelta(5, 7, 2f);
            Assert.Equal(5f, o.GetDelta(5, 7));

            o.SetDelta(31, 0, 1f);                // same tile (0,0), another cell
            Assert.Equal(1, o.TileCount);

            o.SetDelta(32, 0, 9f);                // tile (1,0)
            Assert.Equal(2, o.TileCount);
            Assert.True(o.TryGetTile(1, 0, out MapSculptTile t10));
            Assert.Equal(9f, t10[0, 0]);

            o.SetDelta(-1, -1, 4f);               // tile (-1,-1), local (31,31)
            Assert.Equal(3, o.TileCount);
            Assert.Equal(4f, o.GetDelta(-1, -1));
            Assert.True(o.TryGetTile(-1, -1, out MapSculptTile tNeg));
            Assert.Equal(4f, tNeg[TerrainSculpt.TileSize - 1, TerrainSculpt.TileSize - 1]);

            Assert.False(o.TryGetTile(99, 99, out _));
        }

        [Fact]
        public void Out_of_bounds_tile_is_refused_by_validate_and_save()
        {
            var registry = MapDocRegistry.CreateDefault();
            MapDocument doc = MinimalDoc(-1f, 20f);
            var o = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides = o;

            o.SetDelta(0, 0, 1f);   // tile (0,0): world extent 0..15.5, inside [-1,20]
            Assert.Empty(MapDocumentValidator.Validate(doc, registry));

            o.SetDelta(32, 0, 1f);  // tile (1,0): world extent 16..31.5, maxX 31.5 > 20
            Assert.Contains(MapDocumentValidator.Validate(doc, registry), e => e.Contains("leaves the document bounds"));
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
        }

        [Fact]
        public void BuildField_composition_is_deterministic_across_two_loads()
        {
            MapDocument doc = MapDocumentFileTests.SampleDoc();
            var o = new MapTerrainOverrides(0.5f);
            o.SetDelta(4, 6, 2.5f);
            o.SetDelta(5, 6, -1f);
            o.SetDelta(-3, 2, 3f);
            doc.TerrainOverrides = o;
            string json = MapDocumentFile.SaveText(doc);

            var registry = MapDocRegistry.CreateDefault();
            var a = MapRuntime.BuildField(MapDocumentFile.LoadText(json), registry);
            var b = MapRuntime.BuildField(MapDocumentFile.LoadText(json), registry);
            for (float x = -20f; x <= 20f; x += 5f)
            for (float z = -20f; z <= 20f; z += 5f)
                Assert.Equal(a.SampleHeight(x, z), b.SampleHeight(x, z));

            // The sculpt actually moved the ground: cell (4,6) center is world (2.0, 3.0), + 2.5 m.
            var plain = MapRuntime.BuildField(MapDocumentFileTests.SampleDoc(), registry);
            Assert.Equal(plain.SampleHeight(2f, 3f) + 2.5f, a.SampleHeight(2f, 3f), 4);
        }

        [Fact]
        public void Sculpt_block_passes_schema_and_a_wrong_length_tile_fails()
        {
            MapDocument doc = MapDocumentFileTests.SampleDoc();
            var o = new MapTerrainOverrides(0.5f);
            o.SetDelta(3, 4, 1f);
            doc.TerrainOverrides = o;
            string json = MapDocumentFile.SaveText(doc);
            string schema = MapDocumentSchema.GetJson();

            ValidationReport ok = JsonSchemaValidator.Validate(json, schema);
            Assert.True(ok.IsValid, string.Join("\n", ok.Errors));

            JsonNode node = JsonNode.Parse(json)!;
            JsonArray deltas = node["terrainOverrides"]!["tiles"]![0]!["deltas"]!.AsArray();
            while (deltas.Count > 3) deltas.RemoveAt(deltas.Count - 1);   // shorter than the required 1024
            ValidationReport bad = JsonSchemaValidator.Validate(node.ToJsonString(), schema);
            Assert.False(bad.IsValid);
        }
    }
}
