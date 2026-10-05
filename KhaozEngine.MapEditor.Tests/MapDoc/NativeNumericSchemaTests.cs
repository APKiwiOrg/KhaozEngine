using System;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The schema and the native loader agree on every canonical numeric identity string, so a
    /// schema-only consumer never accepts a document the engine then refuses, or the reverse.</summary>
    public class NativeNumericSchemaTests
    {
        public static TheoryData<string?, bool> HighWaterMarkCases => new()
        {
            { "0", true },
            { "1", true },
            { "42", true },
            { "999999999999999999", true },
            { "9223372036854775806", true },
            { "9223372036854775807", true },
            { "9223372036854775808", false },
            { "9223372036854775810", false },
            { "9300000000000000000", false },
            { "9999999999999999999", false },
            { "10000000000000000000", false },
            { null, false },
            { "", false },
            { "00", false },
            { "01", false },
            { "-1", false },
            { "+1", false },
            { " 1", false },
            { "1 ", false },
            { "1.5", false },
            { "1e2", false },
            { "123\n", false },
            { "9223372036854775807\n", false },
            { "١", false },
        };

        public static TheoryData<string?, bool> PlacementIdCases => new()
        {
            { "1", true },
            { "42", true },
            { "999999999999999999", true },
            { "9223372036854775806", true },
            { "9223372036854775807", true },
            { null, true },
            { "0", false },
            { "9223372036854775808", false },
            { "9223372036854775810", false },
            { "9300000000000000000", false },
            { "9999999999999999999", false },
            { "10000000000000000000", false },
            { "", false },
            { "01", false },
            { "-1", false },
            { "+1", false },
            { " 1", false },
            { "1 ", false },
            { "1.5", false },
            { "1e2", false },
            { "123\n", false },
            { "9223372036854775807\n", false },
            { "١", false },
        };

        [Theory]
        [MemberData(nameof(HighWaterMarkCases))]
        public void HighWaterMark_SchemaAndLoaderAgree(string? value, bool valid)
        {
            JsonObject root = Root(highWaterMark: Node(value), placementId: null);
            AssertParity(root.ToJsonString(), valid);
        }

        [Theory]
        [MemberData(nameof(PlacementIdCases))]
        public void PlacementNumericId_SchemaAndLoaderAgree(string? value, bool valid)
        {
            JsonObject root = Root(highWaterMark: Node("9223372036854775807"), placementId: Node(value));
            AssertParity(root.ToJsonString(), valid);
        }

        /// <summary>The saved sample with one placement, every other numeric identity field fixed at a valid value,
        /// so the field under test is the only reason either validator could refuse the document.</summary>
        private static JsonObject Root(JsonNode? highWaterMark, JsonNode? placementId)
        {
            JsonObject root = JsonNode.Parse(MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc()))!.AsObject();
            Assert.Single(root["placements"]!.AsArray());
            root["numericIdHighWaterMark"] = highWaterMark;
            root["placements"]![0]!["numericId"] = placementId;
            return root;
        }

        private static JsonNode? Node(string? value) => value is null ? null : JsonValue.Create(value);

        private static void AssertParity(string json, bool valid)
        {
            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Exception? loadError = Record.Exception(() => MapDocumentFile.LoadText(json));
            Assert.True(report.IsValid == valid, $"schema valid={report.IsValid}, expected {valid}: {string.Join("\n", report.Errors)}");
            Assert.True((loadError is null) == valid, $"loader valid={loadError is null}, expected {valid}: {loadError?.Message}");
            if (loadError is not null) Assert.IsType<MapDocumentException>(loadError);
        }
    }
}
