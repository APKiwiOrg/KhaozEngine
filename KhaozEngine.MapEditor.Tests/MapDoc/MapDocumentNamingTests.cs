using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Headless tests for the optional, unique-when-named Name on terrain features and exclusions:
    /// round trip, schema acceptance, no-bloat emission for unnamed elements, and validator uniqueness.</summary>
    public class MapDocumentNamingTests
    {
        [Fact]
        public void FeatureName_RoundTripsThroughSaveLoad()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Terrain.Features[0].Name = "north-lake";
            // Features[1] stays unnamed (default null).

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);

            Assert.Equal("north-lake", back.Terrain.Features[0].Name);
            Assert.Null(back.Terrain.Features[1].Name);
        }

        [Fact]
        public void ExclusionName_RoundTripsAndPassesSchema()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Exclusions[0].Name = "market-clearing";

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);
            Assert.Equal("market-clearing", back.Exclusions[0].Name);

            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void UnnamedElements_OmitNameFromJson()
        {
            // SampleDoc's features and exclusions are unnamed by default: the emitted JSON must not carry a
            // "name" key at all for them, so an unnamed element does not bloat every document.
            var doc = MapDocumentFileTests.SampleDoc();
            string json = MapDocumentFile.SaveText(doc);

            JsonNode node = JsonNode.Parse(json)!;
            JsonArray features = node["terrain"]!["features"]!.AsArray();
            Assert.NotEmpty(features);
            foreach (JsonNode? feature in features)
                Assert.False(feature!.AsObject().ContainsKey("name"), "unnamed feature must omit the name key");
            JsonArray exclusions = node["exclusions"]!.AsArray();
            Assert.NotEmpty(exclusions);
            foreach (JsonNode? exclusion in exclusions)
                Assert.False(exclusion!.AsObject().ContainsKey("name"), "unnamed exclusion must omit the name key");
        }

        [Fact]
        public void DuplicateFeatureNames_FailValidation()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Terrain.Features[0].Name = "clearing";
            doc.Terrain.Features[1].Name = "clearing";

            Assert.Contains(MapDocumentValidator.Validate(doc, registry), e => e.Contains("clearing"));
            doc.Terrain.Features[1].Name = "clearing-south";
            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }

        [Fact]
        public void EmptyFeatureNames_DoNotCollide()
        {
            // Null and explicit empty both mean unnamed, and neither collides with the other (or itself).
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Terrain.Features[0].Name = "";
            doc.Terrain.Features[1].Name = null;

            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }

        [Fact]
        public void DuplicateExclusionNames_FailValidation()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Exclusions[0].Name = "market";
            doc.Exclusions.Add(new MapExclusion
            {
                Shape = new DiscShapeDoc { CenterX = 5f, CenterZ = 5f, Radius = 3f },
                Name = "market",
            });

            Assert.Contains(MapDocumentValidator.Validate(doc, registry), e => e.Contains("market"));
            doc.Exclusions[1].Name = "market-south";
            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }

        [Fact]
        public void EmptyExclusionNames_DoNotCollide()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 5f, CenterZ = 5f, Radius = 3f } });

            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }

        [Fact]
        public void ScatterOverrideName_RoundTripsAndPassesSchema()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.ScatterOverrides[0].Name = "low-density-band";

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);
            Assert.Equal("low-density-band", back.ScatterOverrides[0].Name);

            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void UnnamedScatterOverride_OmitsNameFromJson()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            string json = MapDocumentFile.SaveText(doc);

            JsonNode node = JsonNode.Parse(json)!;
            JsonArray overrides = node["scatterOverrides"]!.AsArray();
            Assert.NotEmpty(overrides);
            foreach (JsonNode? scatterOverride in overrides)
                Assert.False(scatterOverride!.AsObject().ContainsKey("name"), "unnamed scatter override must omit the name key");
        }

        [Fact]
        public void DuplicateScatterOverrideNames_FailValidation()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.ScatterOverrides[0].Name = "wetland";
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc
            {
                Shape = new DiscShapeDoc { CenterX = 5f, CenterZ = 5f, Radius = 3f },
                Name = "wetland",
            });

            Assert.Contains(MapDocumentValidator.Validate(doc, registry), e => e.Contains("wetland"));
            doc.ScatterOverrides[1].Name = "wetland-south";
            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }

        [Fact]
        public void EmptyScatterOverrideNames_DoNotCollide()
        {
            var registry = MapDocRegistry.CreateDefault();
            var doc = MapDocumentFileTests.SampleDoc();
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new DiscShapeDoc { CenterX = 5f, CenterZ = 5f, Radius = 3f } });

            Assert.Empty(MapDocumentValidator.Validate(doc, registry));
        }
    }
}
