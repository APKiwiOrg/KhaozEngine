using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Headless tests for MapRuntime: document to TerrainConfig/Field parity with hand-built configs,
    /// scatter config assembly (clearing zeroed, layer-filtered exclusions/overrides), companion assembly, and
    /// placement ground-snapping.</summary>
    public class MapRuntimeTests
    {
        [Fact]
        public void BuildField_MatchesHandBuiltConfig()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            var field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());

            var reference = new TerrainField(new TerrainConfig
            {
                Seed = 7345,
                WaterLevel = -0.5f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Marsh, BaseHeight = 1.5f, HillAmplitude = 1.2f },
                },
                Features = new ITerrainFeature[]
                {
                    new LakeFeature(34f, -14f, 22f, 6f),
                    new FlattenFeature(-32f, 22f, 34f, 2f, 0.25f),
                },
            });

            foreach ((float x, float z) in new[] { (0f, 0f), (34f, -14f), (-32f, 22f), (60f, 60f), (-80f, 10f) })
                Assert.Equal(reference.SampleHeight(x, z), field.SampleHeight(x, z), 5);
            Assert.Equal(reference.WaterLevel, field.WaterLevel);
            Assert.Equal(BiomeId.Marsh, field.SampleBiome(0f, 0f));
        }

        [Fact]
        public void BuildTerrainConfig_OpenBandEdges_MapNullToInfinity()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Terrain.Biomes[0].Start = null;
            doc.Terrain.Biomes[0].End = 50f;
            var cfg = MapRuntime.BuildTerrainConfig(doc, MapDocRegistry.CreateDefault());
            Assert.True(float.IsNegativeInfinity(cfg.Biomes![0].Start));
            Assert.Equal(50f, cfg.Biomes[0].End);
        }

        [Fact]
        public void BuildScatterConfig_AssemblesLayerWithZeroClearing()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            var cfg = MapRuntime.BuildScatterConfig(doc, "trees");
            Assert.Equal(0x52424E, cfg.Seed);
            Assert.Equal(5f, cfg.CellSize);
            Assert.Equal(0f, cfg.ClearingRadius);
            Assert.Single(cfg.Biomes);
            Assert.Equal("pine_a", cfg.Biomes[0].Kinds[0].Id);
            Assert.Single(cfg.Exclusions);       // the sample's exclusion has no layer filter (applies to all)
            Assert.Single(cfg.Overrides);        // the sample's override targets "trees" explicitly
        }

        [Fact]
        public void BuildScatterConfig_LayerFilters_Apply()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "grass", CellSize = 2f });
            // Sample override targets only "trees", sample exclusion targets all layers.
            var grass = MapRuntime.BuildScatterConfig(doc, "grass");
            Assert.Single(grass.Exclusions);
            Assert.Empty(grass.Overrides);
        }

        [Fact]
        public void BuildScatterConfig_UnknownLayer_Throws()
        {
            Assert.Throws<MapDocumentException>(() =>
                MapRuntime.BuildScatterConfig(MapDocumentFileTests.SampleDoc(), "nope"));
        }

        [Fact]
        public void BuildScatterConfigs_ReturnsEveryLayer()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            var all = MapRuntime.BuildScatterConfigs(doc);
            Assert.Single(all);
            Assert.Contains("trees", all.Keys);
        }

        [Fact]
        public void BuildCompanionConfig_Assembles()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            var cfg = MapRuntime.BuildCompanionConfig(doc, "understory");
            Assert.Contains("pine_a", cfg.HostKinds);
            Assert.Equal("fern", cfg.Kinds[0].Id);
        }

        [Fact]
        public void BuildPlacements_GroundSnapsNullY_KeepsExplicitY()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Placements.Add(new MapPlacement { Id = "float", Kind = "rock_a", X = 10f, Z = 10f, Y = 99f });
            var field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
            var placements = MapRuntime.BuildPlacements(doc, field);

            Assert.Equal(2, placements.Count);
            PropPlacement inn = placements.First(p => p.Id == "building_inn");
            Assert.Equal(field.SampleHeight(-30f, 20f), inn.Y, 5);
            Assert.Equal(1.2f, inn.Yaw, 5);
            Assert.Equal(0, inn.Variant);
            PropPlacement floated = placements.First(p => p.Id == "rock_a");
            Assert.Equal(99f, floated.Y);
        }
    }
}
