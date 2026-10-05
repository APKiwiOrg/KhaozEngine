using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The embedded map document schema accepts what SaveText produces and rejects structural garbage.</summary>
    public class MapDocumentSchemaTests
    {
        [Fact]
        public void SchemaJson_IsAvailable()
        {
            string schema = MapDocumentSchema.GetJson();
            Assert.Contains("\"$id\"", schema);
            Assert.Contains("formatVersion", schema);
        }

        [Fact]
        public void SavedDocument_PassesSchema()
        {
            string json = MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc());
            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void MissingRequiredSections_FailSchema()
        {
            ValidationReport report = JsonSchemaValidator.Validate("{\"id\":\"x\"}", MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        [Fact]
        public void WrongTypes_FailSchema()
        {
            string json = MapDocumentFileTests.WithFormatVersion(
                MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()), "\"one\"");
            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        [Fact]
        public void UnknownPropertyAtRoot_FailsSchema()
        {
            JsonNode node = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!;
            node["unknownRootField"] = "surprise";
            ValidationReport report = JsonSchemaValidator.Validate(node.ToJsonString(), MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        [Fact]
        public void UnknownPropertyOnPlacement_FailsSchema()
        {
            JsonNode node = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!;
            node["placements"]![0]!["unknownField"] = "surprise";
            ValidationReport report = JsonSchemaValidator.Validate(node.ToJsonString(), MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        [Fact]
        public void UnknownPropertyOnShape_FailsSchema()
        {
            JsonNode node = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!;
            node["exclusions"]![0]!["shape"]!["unknownShapeField"] = "surprise";
            ValidationReport report = JsonSchemaValidator.Validate(node.ToJsonString(), MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>The schema holds a native asset digest to the rule closure loading enforces: exactly 64
        /// lowercase ASCII hexadecimal characters, with nothing after them.</summary>
        [Theory]
        [InlineData(Digest, true)]
        [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", true)]
        [InlineData("", false)]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
        [InlineData(Digest + "0", false)]
        [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
        [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
        [InlineData(" 123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
        [InlineData(Digest + "\n", false)]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n", false)]
        public void NativeAssetDigest_MustBeLowercaseSha256Hex(string sha256, bool valid)
        {
            JsonNode node = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!;
            node["nativeAssets"] = new JsonArray(new JsonObject
            {
                ["id"] = "root",
                ["path"] = "assets/root.manifest.json",
                ["sha256"] = sha256,
                ["payloadVersion"] = 1,
            });
            ValidationReport report = JsonSchemaValidator.Validate(node.ToJsonString(), MapDocumentSchema.GetJson());
            Assert.True(report.IsValid == valid, $"schema valid={report.IsValid}, expected {valid}: {string.Join("\n", report.Errors)}");
        }

        [Fact]
        public void UnknownPropertyOnFeature_StillPassesSchema()
        {
            JsonNode node = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!;
            node["terrain"]!["features"]![0]!["customGameField"] = "totally fine";
            ValidationReport report = JsonSchemaValidator.Validate(node.ToJsonString(), MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }
    }
}
