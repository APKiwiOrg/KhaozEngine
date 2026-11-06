using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeJsonAdmissionRefusalTests
{
    [Theory]
    [InlineData("{}", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("{\"type\":\"volcano\"}", MapDocumentRefusal.DocumentTerrainFeatureUnknown, "volcano")]
    public void FeatureDiscriminator_ReportsTheRequiredOrUnregisteredType(string json, MapDocumentRefusal reason, string? type)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapFeatureConverter(MapDocRegistry.CreateDefault()));
        var error = Assert.Throws<MapFeatureJsonException>(() => JsonSerializer.Deserialize<MapFeature>(json, options));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("$.type", error.FieldPath);
        Assert.Equal(type, error.ActualText);
        Assert.IsType<LakeFeatureDoc>(JsonSerializer.Deserialize<MapFeature>("{\"type\":\"lake\"}", options));
    }

    [Fact]
    public void FeatureDeserializer_RefusesAConsumerConverterReturningNull()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapFeatureConverter(MapDocRegistry.CreateDefault()));
        options.Converters.Add(new NullLakeConverter());
        var error = Assert.Throws<MapFeatureJsonException>(() => JsonSerializer.Deserialize<MapFeature>("{\"type\":\"lake\"}", options));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal("lake", error.ActualText);
        var control = new JsonSerializerOptions();
        control.Converters.Add(new MapFeatureConverter(MapDocRegistry.CreateDefault()));
        Assert.IsType<LakeFeatureDoc>(JsonSerializer.Deserialize<MapFeature>("{\"type\":\"lake\"}", control));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("\"\"", "")]
    [InlineData("\"00\"", "00")]
    [InlineData("\"10000000000000000000\"", "10000000000000000000")]
    public void NumericIdentity_ReportsNonStringOrNoncanonicalText(string json, string? text)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapNumericIdJsonConverter());
        var error = Assert.Throws<MapNumericIdJsonException>(() => JsonSerializer.Deserialize<long>(json, options));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(MapDocumentRefusal.SchemaInt64Malformed, error.Reason);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal(text, error.ActualText);
        Assert.Equal(0, error.MinimumInteger);
        Assert.Equal(long.MaxValue, error.MaximumInteger);
        Assert.Equal(long.MaxValue, JsonSerializer.Deserialize<long>("\"9223372036854775807\"", options));
        Assert.Equal(0, JsonSerializer.Deserialize<long>("\"0\"", options));
        var root = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
        root["numericIdHighWaterMark"] = JsonNode.Parse(json);
        var wrapped = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString(), sourcePath: "numeric"));
        Assert.Equal(error.Reason, wrapped.Reason);
        Assert.Equal(error.ActualText, wrapped.ActualText);
        Assert.Equal(error.MinimumInteger, wrapped.MinimumInteger);
        Assert.Equal(error.MaximumInteger, wrapped.MaximumInteger);
        Assert.Equal("numeric", wrapped.SourceName);
        root["numericIdHighWaterMark"] = "0";
        Assert.Equal(0, MapDocumentFile.LoadText(root.ToJsonString()).NumericIdHighWaterMark);
    }

    [Theory]
    [InlineData("{}", MapDocumentRefusal.SchemaMemberMissing, null)]
    [InlineData("{\"formatVersion\":null}", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("{\"formatVersion\":\"3\"}", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("{\"formatVersion\":5}", MapDocumentRefusal.DocumentMigrationSourceVersionInvalid, 5)]
    public void NativeMigration_ReportsItsSourceVersionWithoutMutatingIt(string json, MapDocumentRefusal reason, int? version)
    {
        var source = JsonNode.Parse(json)!.AsObject();
        string before = source.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => MapNativeMigration.Upgrade(source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("$.formatVersion", error.FieldPath);
        Assert.Equal(version, error.ActualVersion);
        Assert.Equal(new[] { 3, 4 }, error.SupportedVersions);
        Assert.Equal(before, source.ToJsonString());
        source["formatVersion"] = 3;
        Assert.Equal(4, MapNativeMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
        source["formatVersion"] = 4;
        Assert.Equal(4, MapNativeMigration.Upgrade(source)["formatVersion"]!.GetValue<int>());
    }

    sealed class NullLakeConverter : JsonConverter<LakeFeatureDoc>
    {
        public override LakeFeatureDoc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return null;
        }
        public override void Write(Utf8JsonWriter writer, LakeFeatureDoc value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
