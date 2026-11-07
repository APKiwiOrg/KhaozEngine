using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageJsonRefusalTests
{
    [Theory]
    [InlineData("set-object", MapDocumentRefusal.SchemaObjectRequired, "$", null)]
    [InlineData("layer-object", MapDocumentRefusal.SchemaObjectRequired, "$.layers[0]", 0)]
    [InlineData("support-object", MapDocumentRefusal.SchemaObjectRequired, "$.layers[0].support", 0)]
    [InlineData("model-object", MapDocumentRefusal.SchemaObjectRequired, "$.layers[0].models[0]", 0)]
    [InlineData("policy-object", MapDocumentRefusal.SchemaObjectRequired, "$.layers[0].policy", 0)]
    [InlineData("layers-array", MapDocumentRefusal.SchemaArrayRequired, "$.layers", null)]
    [InlineData("models-array", MapDocumentRefusal.SchemaArrayRequired, "$.layers[0].models", 0)]
    [InlineData("set-unknown", MapDocumentRefusal.SchemaMemberUnknown, "$.extra", null)]
    [InlineData("special-member", MapDocumentRefusal.SchemaMemberUnknown, "$['extra.member']", null)]
    [InlineData("set-duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.payloadVersion", null)]
    [InlineData("set-null", MapDocumentRefusal.SchemaValueRequired, "$.payloadVersion", null)]
    [InlineData("layer-missing", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].seed", 0)]
    [InlineData("support-kind-missing", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].support.kind", 0)]
    [InlineData("id-type", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].id", 0)]
    [InlineData("set-payload-two", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion", null)]
    [InlineData("layer-payload-two", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.layers[0].payloadVersion", 0)]
    [InlineData("cell-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.layers[0].cellSize", 0)]
    [InlineData("cell-overflow-positive", MapDocumentRefusal.ValueNonfinite, "$.layers[0].cellSize", 0)]
    [InlineData("cell-overflow-negative", MapDocumentRefusal.ValueNonfinite, "$.layers[0].cellSize", 0)]
    [InlineData("width-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].width", 0)]
    [InlineData("seed-overflow", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].seed", 0)]
    [InlineData("material-id-overflow", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].materials[2].id", 2)]
    [InlineData("allowed-fraction", MapDocumentRefusal.ValueOutsideRange, "$.layers[0].allowedVisibleMaterials[0]", 0)]
    [InlineData("exclude-type", MapDocumentRefusal.SchemaBooleanRequired, "$.layers[0].excludeIndoors", 0)]
    [InlineData("density-base64", MapDocumentRefusal.SchemaBase64Malformed, "$.layers[0].chunks[0].density", 0)]
    [InlineData("density-noncanonical", MapDocumentRefusal.SchemaBase64Malformed, "$.layers[0].chunks[0].density", 0)]
    [InlineData("density-1023", MapDocumentRefusal.SchemaArrayLengthMismatch, "$.layers[0].chunks[0].density", 0)]
    [InlineData("density-numeric-negative", MapDocumentRefusal.SchemaValueRequired, "$.layers[0].chunks[0].density", 0)]
    [InlineData("support-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].support.kind", 0)]
    [InlineData("material-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].materials[2].kind", 2)]
    [InlineData("row-unknown", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].rowDirection", 0)]
    [InlineData("rule-kind-future", MapDocumentRefusal.SchemaEnumUnsupported, "$.layers[0].clearanceRules[0].kind", 0)]
    public void Codec_refusals_identify_the_rule_and_typed_subject(string variant, MapDocumentRefusal reason, string path, int? index)
    {
        string text = Section(variant);
        using JsonDocument json = JsonDocument.Parse(text);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageJson.Read(json.RootElement));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        Assert.Equal(index, error.Index);
        if (variant is "support-object" or "model-object" or "policy-object" or "support-kind-missing" or
            "cell-nonfinite" or "cell-overflow-positive" or "cell-overflow-negative" or "width-fraction" or "seed-overflow" or "material-id-overflow" or "allowed-fraction" or
            "exclude-type" or "density-base64" or "density-noncanonical" or "density-1023" or "density-numeric-negative" or
            "support-kind-future" or "material-kind-future" or "row-unknown" or "rule-kind-future")
            Assert.Equal(FoliageFixtures.LayerId, error.LayerId);
        else Assert.Null(error.LayerId);
        if (variant.StartsWith("density-", StringComparison.Ordinal))
            Assert.Equal(new MapFoliageChunkKey(0, 0), error.FoliageChunk);
        if (variant == "density-1023")
        {
            Assert.Equal(1023, error.ActualByteCount);
            Assert.Equal(MapFoliageChunk.ByteLength, error.ExpectedByteCount);
        }
        if (variant is "set-payload-two" or "layer-payload-two")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        if (variant == "cell-nonfinite") Assert.Equal(1e39, error.ActualNumber);
        // A literal beyond double range has no representable value to report.
        if (variant is "cell-overflow-positive" or "cell-overflow-negative") Assert.Null(error.ActualNumber);
        if (variant == "width-fraction") Assert.Equal(35.5, error.ActualNumber);
        if (variant == "seed-overflow") Assert.Equal(2147483648L, error.ActualInteger);
        if (variant is "width-fraction" or "seed-overflow")
        {
            Assert.Equal(int.MinValue, error.MinimumInteger);
            Assert.Equal(int.MaxValue, error.MaximumInteger);
        }
        if (variant is "material-id-overflow" or "allowed-fraction")
        {
            Assert.Equal(0, error.MinimumInteger);
            Assert.Equal(ushort.MaxValue, error.MaximumInteger);
            Assert.Equal(variant == "material-id-overflow" ? 65536 : 10.5, error.ActualNumber);
        }
        using JsonDocument control = JsonDocument.Parse(Control(variant));
        Assert.Single(MapFoliageJson.Read(control.RootElement).Layers);
    }

    [Fact]
    public void Null_section_keeps_its_reason_through_the_document_boundary()
    {
        var error = Assert.Throws<MapFoliageJsonException>(() => JsonSerializer.Deserialize<MapFoliageSet>("null"));
        Assert.Equal(MapDocumentRefusal.FoliageSectionNull, error.Reason);
        Assert.Equal("$", error.FieldPath);
        var document = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(FoliageFixtures.Malformed("root-null")));
        Assert.Equal(error.Reason, document.Reason);
        Assert.Equal(error.FieldPath, document.FieldPath);
        Assert.NotNull(MapDocumentFile.LoadText(FoliageFixtures.Malformed("control")).Foliage);
    }

    [Theory]
    [InlineData("density-1023", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("layer-payload-two", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("cell-nonfinite", MapDocumentRefusal.ValueNonfinite)]
    public void Converter_and_document_wrappers_keep_known_identifiers(string variant, MapDocumentRefusal reason)
    {
        var converter = Assert.Throws<MapFoliageJsonException>(() => JsonSerializer.Deserialize<MapFoliageSet>(Section(variant)));
        Assert.Equal(reason, converter.Reason);
        var inner = Assert.IsType<MapDocumentException>(converter.InnerException);
        Assert.Equal(inner.FieldPath, converter.FieldPath);
        Assert.Equal(inner.LayerId, converter.LayerId);
        Assert.Equal(inner.Index, converter.Index);
        Assert.Equal(inner.FoliageChunk, converter.FoliageChunk);
        Assert.Equal(inner.ActualByteCount, converter.ActualByteCount);
        Assert.Equal(inner.ExpectedByteCount, converter.ExpectedByteCount);
        Assert.Equal(inner.ActualVersion, converter.ActualVersion);
        Assert.Equal(inner.SupportedVersions, converter.SupportedVersions);
        Assert.Equal(inner.ActualNumber, converter.ActualNumber);
        MapDocumentException outer = MapDocumentJsonErrors.Wrap(converter, "map.json", "document refused");
        Assert.Equal(reason, outer.Reason);
        Assert.Same(inner, outer.InnerException);
        Assert.Equal("map.json", outer.SourceName);
        Assert.Equal(inner.FieldPath, outer.FieldPath);
        Assert.Equal(inner.LayerId, outer.LayerId);
        Assert.Equal(inner.Index, outer.Index);
        Assert.Equal(inner.FoliageChunk, outer.FoliageChunk);
        Assert.Equal(inner.ActualByteCount, outer.ActualByteCount);
        Assert.Equal(inner.ExpectedByteCount, outer.ExpectedByteCount);
        Assert.Equal(inner.ActualNumber, outer.ActualNumber);
        Assert.Equal(inner.ActualVersion, outer.ActualVersion);
        Assert.Equal(inner.SupportedVersions, outer.SupportedVersions);
        Assert.NotNull(JsonSerializer.Deserialize<MapFoliageSet>(Section("control")));
    }

    [Fact]
    public void Support_kind_refusal_keeps_its_rule_and_subject_through_wrappers()
    {
        var converter = Assert.Throws<MapFoliageJsonException>(() => JsonSerializer.Deserialize<MapFoliageSet>(Section("support-kind-future")));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, converter.Reason);
        Assert.Equal("$.layers[0].support.kind", converter.FieldPath);
        Assert.Equal("Volume", converter.ActualText);
        Assert.Equal(typeof(MapSupportBindingKind), converter.EnumType);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(FoliageFixtures.Malformed("support-kind-future")));
        Assert.Equal(converter.Reason, error.Reason);
        Assert.Equal(converter.FieldPath, error.FieldPath);
        Assert.Equal(converter.ActualText, error.ActualText);
        Assert.Equal(converter.EnumType, error.EnumType);
        Assert.NotNull(MapDocumentFile.LoadText(FoliageFixtures.Malformed("control")).Foliage);
    }

    [Fact]
    public void Supported_versions_are_owned_read_only_snapshots()
    {
        int[] source = [1];
        var error = new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "future payload") { SupportedVersions = source };
        var json = new MapFoliageJsonException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "future payload") { SupportedVersions = source };
        var finding = new MapDocumentFinding(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "future payload") { SupportedVersions = source };
        source[0] = 2;
        Assert.Equal(new[] { 1 }, error.SupportedVersions);
        Assert.Equal(new[] { 1 }, json.SupportedVersions);
        Assert.Equal(new[] { 1 }, finding.SupportedVersions);
        if (error.SupportedVersions is IList<int> list) Assert.Throws<NotSupportedException>(() => list[0] = 3);
        if (json.SupportedVersions is IList<int> jsonList) Assert.Throws<NotSupportedException>(() => jsonList[0] = 3);
        if (finding.SupportedVersions is IList<int> findingList) Assert.Throws<NotSupportedException>(() => findingList[0] = 3);
    }

    const double OverflowMarker = 12345.25;

    static string Section(string variant)
    {
        if (variant == "set-object") return "[]";
        string fixtureVariant = variant is "layer-object" or "support-object" or "model-object" or "policy-object" or
            "layers-array" or "models-array" or "special-member" or "support-kind-missing" or "id-type" or "seed-overflow" or "exclude-type" or
            "cell-overflow-positive" or "cell-overflow-negative"
                ? "control" : variant;
        // JsonDocument retains duplicate members, which JsonNode cannot represent.
        using JsonDocument fixture = JsonDocument.Parse(FoliageFixtures.Malformed(fixtureVariant));
        string section = fixture.RootElement.GetProperty("foliage").GetRawText();
        if (fixtureVariant != "control") return section;
        JsonObject root = JsonNode.Parse(section)!.AsObject();
        JsonObject layer = root["layers"]![0]!.AsObject();
        switch (variant)
        {
            case "layer-object": root["layers"]![0] = false; break;
            case "support-object": layer["support"] = false; break;
            case "model-object": layer["models"]![0] = false; break;
            case "policy-object": layer["policy"] = false; break;
            case "layers-array": root["layers"] = false; break;
            case "models-array": layer["models"] = false; break;
            case "special-member": root["extra.member"] = false; break;
            case "support-kind-missing": layer["support"]!.AsObject().Remove("kind"); break;
            case "id-type": layer["id"] = false; break;
            case "seed-overflow": layer["seed"] = 2147483648L; break;
            case "exclude-type": layer["excludeIndoors"] = "true"; break;
            case "cell-overflow-positive" or "cell-overflow-negative": layer["cellSize"] = OverflowMarker; break;
        }
        // JsonNode cannot hold a number beyond double range, so the overflow literal replaces a unique marker as text.
        string text = root.ToJsonString();
        return variant switch
        {
            "cell-overflow-positive" => text.Replace($"\"cellSize\":{OverflowMarker.ToString(CultureInfo.InvariantCulture)}", "\"cellSize\":1e400", StringComparison.Ordinal),
            "cell-overflow-negative" => text.Replace($"\"cellSize\":{OverflowMarker.ToString(CultureInfo.InvariantCulture)}", "\"cellSize\":-1e400", StringComparison.Ordinal),
            _ => text,
        };
    }

    static string Control(string variant)
    {
        JsonObject root = JsonNode.Parse(Section("control"))!.AsObject();
        JsonObject layer = root["layers"]![0]!.AsObject();
        // Scalar controls sit on the refused rule's boundary, independent of later semantic validation.
        if (variant == "width-fraction") layer["width"] = int.MaxValue;
        if (variant == "seed-overflow") layer["seed"] = int.MaxValue;
        if (variant == "material-id-overflow") layer["materials"]![2]!["id"] = ushort.MaxValue;
        if (variant == "allowed-fraction") layer["allowedVisibleMaterials"] = new JsonArray(0);
        if (variant is "cell-nonfinite" or "cell-overflow-positive" or "cell-overflow-negative") layer["cellSize"] = float.MaxValue;
        return root.ToJsonString();
    }
}
