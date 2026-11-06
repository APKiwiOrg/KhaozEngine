using System;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabSchemaAdmissionTests
{
    const string Storey = """{"type":"Storey","key":{"kind":"Storey","key":"s"},"name":"level"}""";
    const string Adoption = """[{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{"explicitY":0}}]""";

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(65, 64, 64, 64)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(64, 65, 64, 64)]
    public void Patch_dimensions_identify_both_axes_and_accept_the_exact_boundary(int width, int depth, int acceptedWidth, int acceptedDepth)
    {
        using var refused = JsonDocument.Parse($"{{\"patches\":[{{\"width\":{width},\"depth\":{depth}}}]}}");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Preflight(refused.RootElement));
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, error.Reason);
        Assert.Equal(width, error.Width);
        Assert.Equal(depth, error.Depth);
        Assert.Equal("$.patches[0]", error.FieldPath);
        using var accepted = JsonDocument.Parse($"{{\"patches\":[{{\"width\":{acceptedWidth},\"depth\":{acceptedDepth}}}]}}");
        MapPrefabJson.Preflight(accepted.RootElement);
    }

    [Theory]
    [InlineData("""{"children":[{"notes.extra":{"x":1,"X":2}}]}""", """{"children":[{"notes.extra":{"x":1}}]}""", MapDocumentRefusal.SchemaMemberDuplicate, "$.children[0]['notes.extra']")]
    [InlineData("""{"children":[{"notes[extra]":{"x":1,"x":2}}]}""", """{"children":[{"notes[extra]":{"x":1}}]}""", MapDocumentRefusal.SchemaMemberDuplicate, "$.children[0]['notes[extra]']")]
    [InlineData("""{"children":[{"key":null}]}""", """{"children":[{"key":{}}]}""", MapDocumentRefusal.SchemaValueRequired, "$.children[0].key")]
    [InlineData("""{"heightMode":"Support","supportBinding":{"surfaceId":null}}""", """{"heightMode":"Support","supportBinding":{"surfaceId":"floor"}}""", MapDocumentRefusal.SchemaValueRequired, "$.supportBinding.surfaceId")]
    [InlineData("null", "{}", MapDocumentRefusal.SchemaValueRequired, "$")]
    public void Preflight_identifies_the_actual_nested_field(string refused, string accepted, MapDocumentRefusal reason, string fieldPath)
    {
        using var invalid = JsonDocument.Parse(refused);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Preflight(invalid.RootElement));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fieldPath, error.FieldPath);
        using var valid = JsonDocument.Parse(accepted);
        MapPrefabJson.Preflight(valid.RootElement);
    }

    [Theory]
    [InlineData("""{"type":"Storey","key":{"kind":"Storey","key":"s"}}""", MapDocumentRefusal.SchemaMemberMissing, "$.name")]
    [InlineData("""{"type":"Storey","key":{"kind":"Storey","key":"s"},"name":"level","notes.extra":1}""", MapDocumentRefusal.SchemaMemberUnknown, "$['notes.extra']")]
    [InlineData("""{"type":"Storey","key":{"kind":"Storey","key":"s"},"name":"level","notes[extra]":1}""", MapDocumentRefusal.SchemaMemberUnknown, "$['notes[extra]']")]
    public void Closed_child_members_identify_the_missing_or_unknown_property(string refused, MapDocumentRefusal reason, string fieldPath)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadChild(refused));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fieldPath, error.FieldPath);
        Assert.IsType<MapPrefabStoreyChild>(MapPrefabJson.ReadChild(Storey));
    }

    [Fact]
    public void Placement_payload_requires_an_object_at_its_value_field()
    {
        const string refused = """{"type":"Placement","key":{"kind":"Placement","key":"p"},"value":[]}""";
        const string accepted = """{"type":"Placement","key":{"kind":"Placement","key":"p"},"value":{"id":"p","kind":"mesh","assetId":"a","x":0,"z":0,"yaw":0,"scale":1,"tags":[]}}""";
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadChild(refused));
        Assert.Equal(MapDocumentRefusal.SchemaObjectRequired, error.Reason);
        Assert.Equal("$.value", error.FieldPath);
        Assert.IsType<MapPrefabPlacementChild>(MapPrefabJson.ReadChild(accepted));
    }

    [Theory]
    [InlineData("[]", "{}", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("""{"roles":null}""", "{}", MapDocumentRefusal.SchemaValueRequired, "$.roles")]
    [InlineData("""{"roles":{"playerStartRole":null,"spawnRole":"spawn","landmarkRole":"landmark"}}""", """{"roles":{"playerStartRole":"entry","spawnRole":"spawn","landmarkRole":"landmark"}}""", MapDocumentRefusal.SchemaValueRequired, "$.roles.playerStartRole")]
    public void Marker_options_identify_the_refused_option_or_role(string refused, string accepted, MapDocumentRefusal reason, string fieldPath)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerOptions(refused));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fieldPath, error.FieldPath);
        Assert.NotNull(MapPrefabJson.ReadMarkerOptions(accepted));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("markerId")]
    [InlineData("name")]
    [InlineData("role")]
    public void Adoption_requires_each_named_identity_field(string field)
    {
        string refused = Adoption.Replace("\"" + field + "\":\"" + (field switch
        { "id" => "s", "markerId" => "m", "name" => "n", _ => "r" }) + "\"", "\"" + field + "\":\"\"", StringComparison.Ordinal);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerAdoption(refused));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$[0]." + field, error.FieldPath);
        Assert.Single(MapPrefabJson.ReadMarkerAdoption(Adoption));
    }

    [Fact]
    public void Adoption_refuses_a_repeated_source_key_and_keys_on_kind_and_id()
    {
        const string Row = """{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{"explicitY":0}}""";
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerAdoption($"[{Row},{Row}]"));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal("s", error.SourceId);
        Assert.Equal("Spawn", error.SubjectKind);
        Assert.Equal(1, error.Index);
        Assert.Equal("$[1].id", error.FieldPath);
        string otherKind = Row.Replace("\"Spawn\"", "\"PlayerSpawn\"", StringComparison.Ordinal);
        Assert.Equal(2, MapPrefabJson.ReadMarkerAdoption($"[{Row},{otherKind}]").Count);
    }

    [Theory]
    [InlineData("""{}""", null, MapDocumentRefusal.MarkerHeightChoiceInvalid, "$[0].height")]
    [InlineData("""{"explicitY":0,"supportBinding":{"kind":"Surface","surfaceId":"f"}}""", null, MapDocumentRefusal.MarkerHeightChoiceInvalid, "$[0].height")]
    [InlineData("""{}""", "1e400", MapDocumentRefusal.MarkerHeightChoiceInvalid, "$[0].height")]
    [InlineData("""{"explicitY":1e400}""", null, MapDocumentRefusal.ValueNonfinite, "$[0].height.explicitY")]
    [InlineData("""{"explicitY":-1e400}""", "1e400", MapDocumentRefusal.ValueNonfinite, "$[0].height.explicitY")]
    [InlineData("""{"explicitY":0}""", "1e400", MapDocumentRefusal.ValueNonfinite, "$[0].yawRadians")]
    public void Adoption_height_and_yaw_name_the_rule_that_failed_first(string height, string? yaw, MapDocumentRefusal reason, string fieldPath)
    {
        static string Row(string height, string? yaw) =>
            $$"""[{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{{height}}{{(yaw is null ? "" : ",\"yawRadians\":" + yaw)}}}]""";
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerAdoption(Row(height, yaw)));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("m", error.MarkerId);
        Assert.Equal(fieldPath, error.FieldPath);
        Assert.Single(MapPrefabJson.ReadMarkerAdoption(Row("""{"explicitY":3.4028235e38}""", "-3.4028235e38")));
    }

    [Fact]
    public void Placement_numeric_ids_belong_to_instance_bindings()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(PrefabFixtures.AssetBytes("placement-numeric-id"), "hut"));
        Assert.Equal(MapDocumentRefusal.AuthoredOwnershipMismatch, error.Reason);
        Assert.Equal(MapPrefabChildKind.Placement, error.ChildKind);
        Assert.Equal("$.value.numericId", error.FieldPath);
        MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut");
    }

    [Theory]
    [InlineData("null", "$", Adoption)]
    [InlineData("[null]", "$[0]", Adoption)]
    [InlineData("""[{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{"explicitY":null}}]""", "$[0].height.explicitY", Adoption)]
    [InlineData("""[{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{"supportBinding":{"kind":"Surface","surfaceId":null}}}]""", "$[0].height.supportBinding.surfaceId",
        """[{"kind":"Spawn","id":"s","markerId":"m","name":"n","role":"r","height":{"supportBinding":{"kind":"Surface","surfaceId":"f"}}}]""")]
    public void Adoption_explicit_nulls_identify_their_array_or_member_path(string refused, string fieldPath, string accepted)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerAdoption(refused));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(fieldPath, error.FieldPath);
        Assert.Single(MapPrefabJson.ReadMarkerAdoption(accepted));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("enum")]
    [InlineData("file")]
    [InlineData("file-enum")]
    public void Json_wrappers_keep_concrete_refusals_and_reader_metadata(string form)
    {
        // W5c review regression: a deserialization refusal must retain the prefab resource context.
        string payload = form == "file-enum"
            ? "{\"payloadVersion\":1,\"id\":\"p\",\"children\":[" + Storey.Replace("\"kind\":\"Storey\"", "\"kind\":\"Future\"", StringComparison.Ordinal) + "]}"
            : "{ invalid";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        byte[] before = (byte[])bytes.Clone();
        MapDocumentException error = form switch
        {
            "file" or "file-enum" => Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(bytes, "p")),
            "text" => Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadChild("{ invalid")),
            _ => Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadChild(Storey.Replace("\"kind\":\"Storey\"", "\"kind\":\"Future\"", StringComparison.Ordinal))),
        };
        bool enumValue = form is "enum" or "file-enum";
        Assert.Equal(enumValue ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.SchemaMalformed, error.Reason);
        if (enumValue)
        {
            Assert.Equal(typeof(MapPrefabChildKind), error.EnumType);
            Assert.Equal("Future", error.ActualText);
        }
        bool file = form is "file" or "file-enum";
        if (file) Assert.Equal("p", error.ResourceId);
        MapDocumentException? original = form == "file-enum" ? Assert.IsType<MapDocumentException>(error.InnerException) : null;
        if (original is not null)
        {
            Assert.Equal(original.Reason, error.Reason);
            Assert.Equal(original.FieldPath, error.FieldPath);
            Assert.Equal(original.SourceName, error.SourceName);
            Assert.Equal(original.Path, error.Path);
            Assert.Null(original.ResourceId);
        }
        var json = Assert.IsAssignableFrom<JsonException>(original?.InnerException ?? error.InnerException);
        Assert.Equal(json.Path, error.FieldPath);
        Assert.Equal(json.LineNumber, error.LineNumber);
        Assert.Equal(json.BytePositionInLine, error.BytePositionInLine);
        if (file)
        {
            Assert.Equal(before, bytes);
            string accepted = form == "file-enum"
                ? payload.Replace("\"kind\":\"Future\"", "\"kind\":\"Storey\"", StringComparison.Ordinal)
                : "{\"payloadVersion\":1,\"id\":\"p\",\"children\":[]}";
            Assert.Equal("p", MapPrefabFile.Read(Encoding.UTF8.GetBytes(accepted), "p").Id);
        }
        else Assert.IsType<MapPrefabStoreyChild>(MapPrefabJson.ReadChild(Storey));
    }

    [Fact]
    public void Prefab_deserialization_preserves_a_known_inner_refusal_through_the_shared_copy_method()
    {
        using var refused = JsonDocument.Parse("{\"reject\":true}");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Read<MapDocumentSchemaAdmissionTests.RefusingFeature>(refused.RootElement));
        Assert.Equal(MapDocumentRefusal.SchemaMemberMissing, error.Reason);
        Assert.Equal("$.custom.required", error.FieldPath);
        Assert.Equal("custom.json", error.SourceName);
        Assert.Equal("custom.json", error.Path);
        Assert.Equal(2, error.ResolverVersion);
        var original = Assert.IsType<MapDocumentException>(error.InnerException);
        Assert.Equal(original.Reason, error.Reason);
        using var accepted = JsonDocument.Parse("{\"reject\":false}");
        Assert.NotNull(MapPrefabJson.Read<MapDocumentSchemaAdmissionTests.RefusingFeature>(accepted.RootElement));
    }
}
