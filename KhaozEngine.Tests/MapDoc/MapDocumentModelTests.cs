using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Headless tests for the map document model: shape DTO to IArea2D conversion, feature DTO
    /// polymorphic (de)serialization through the registry, registry custom types and duplicate rejection.</summary>
    public class MapDocumentModelTests
    {
        static JsonSerializerOptions Options(MapDocRegistry registry)
        {
            var o = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                AllowOutOfOrderMetadataProperties = true,
            };
            o.Converters.Add(new JsonStringEnumConverter());
            o.Converters.Add(new MapFeatureConverter(registry));
            return o;
        }

        [Fact]
        public void ShapeDocs_ConvertToAreas()
        {
            Assert.True(new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 3f }.ToArea().Contains(1f, 2f));
            Assert.True(new RectShapeDoc { MinX = 0f, MinZ = 0f, MaxX = 2f, MaxZ = 2f }.ToArea().Contains(1f, 1f));
            var poly = new PolygonShapeDoc();
            poly.Points.Add(new[] { 0f, 0f });
            poly.Points.Add(new[] { 4f, 0f });
            poly.Points.Add(new[] { 4f, 4f });
            Assert.True(poly.ToArea().Contains(3f, 1f));
            Assert.False(poly.ToArea().Contains(0f, 3f));
        }

        [Fact]
        public void ShapeDocs_RoundTripPolymorphically()
        {
            var registry = MapDocRegistry.CreateDefault();
            MapShapeDoc shape = new DiscShapeDoc { CenterX = 5f, CenterZ = -3f, Radius = 7f };
            string json = JsonSerializer.Serialize(shape, Options(registry));
            Assert.Contains("\"disc\"", json);
            var back = JsonSerializer.Deserialize<MapShapeDoc>(json, Options(registry));
            var disc = Assert.IsType<DiscShapeDoc>(back);
            Assert.Equal(5f, disc.CenterX);
            Assert.Equal(7f, disc.Radius);
        }

        [Fact]
        public void BuiltInFeatures_RoundTripAndBuild()
        {
            var registry = MapDocRegistry.CreateDefault();
            MapFeature f = new LakeFeatureDoc { CenterX = 34f, CenterZ = -14f, Radius = 22f, Depth = 6f };
            string json = JsonSerializer.Serialize(f, Options(registry));
            Assert.Contains("\"lake\"", json);

            var back = JsonSerializer.Deserialize<MapFeature>(json, Options(registry));
            var lake = Assert.IsType<LakeFeatureDoc>(back);
            Assert.Equal(22f, lake.Radius);

            ITerrainFeature built = registry.BuildFeature(lake);
            var reference = new LakeFeature(34f, -14f, 22f, 6f);
            Assert.Equal(reference.Apply(34f, -14f, 5f), built.Apply(34f, -14f, 5f), 5);
        }

        [Fact]
        public void UnknownFeatureType_ThrowsJsonException()
        {
            var registry = MapDocRegistry.CreateDefault();
            Assert.ThrowsAny<JsonException>(() =>
                JsonSerializer.Deserialize<MapFeature>("{\"type\":\"volcano\",\"x\":1}", Options(registry)));
        }

        sealed class StepFeatureDoc : MapFeature
        {
            public override string Type => "step";
            public float Amount { get; set; }
        }

        sealed class StepFeature : ITerrainFeature
        {
            readonly float _amount;
            public StepFeature(float amount) { _amount = amount; }
            public float Apply(float x, float z, float h) => h + _amount;
        }

        [Fact]
        public void CustomFeature_RegistersRoundTripsAndBuilds()
        {
            var registry = MapDocRegistry.CreateDefault();
            registry.RegisterFeature("step", typeof(StepFeatureDoc), f => new StepFeature(((StepFeatureDoc)f).Amount));

            MapFeature f = new StepFeatureDoc { Amount = 2.5f };
            string json = JsonSerializer.Serialize(f, Options(registry));
            var back = JsonSerializer.Deserialize<MapFeature>(json, Options(registry));
            Assert.Equal(2.5f, Assert.IsType<StepFeatureDoc>(back).Amount);
            Assert.Equal(12.5f, registry.BuildFeature(back!).Apply(0f, 0f, 10f));
        }

        [Fact]
        public void DuplicateFeatureRegistration_Throws()
        {
            var registry = MapDocRegistry.CreateDefault();
            Assert.Throws<ArgumentException>(() =>
                registry.RegisterFeature("lake", typeof(LakeFeatureDoc), f => new LakeFeature(0, 0, 1, 1)));
        }

        [Fact]
        public void RimFeatureDoc_BuildsWithPasses()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = new RimFeatureDoc
            {
                CenterX = 0f, CenterZ = 0f, InnerRadius = 78f, OuterRadius = 116f, WallHeight = 55f,
                Ruggedness = 0.5f, Seed = 5,
            };
            doc.Passes.Add(new RimPassDoc { AngleRadians = MathF.PI / 2f, HalfWidth = 11f, Falloff = 8f });
            ITerrainFeature built = registry.BuildFeature(doc);
            var reference = new RimFeature(new System.Numerics.Vector2(0f, 0f), 78f, 116f, 55f, 0.5f,
                new[] { new RimPass(MathF.PI / 2f, 11f, 8f) }, 5);
            Assert.Equal(reference.Apply(0f, 100f, 0f), built.Apply(0f, 100f, 0f), 4);
            Assert.Equal(reference.Apply(100f, 0f, 0f), built.Apply(100f, 0f, 0f), 4);
        }
    }
}
