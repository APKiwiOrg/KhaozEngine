using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceCodecSchemaRefusalTests
{
    [Theory]
    [InlineData("unknown", MapDocumentRefusal.SchemaMemberUnknown, "extra")]
    [InlineData("duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "width")]
    [InlineData("missing", MapDocumentRefusal.SchemaMemberMissing, "width")]
    [InlineData("object", MapDocumentRefusal.SchemaObjectRequired, null)]
    [InlineData("array", MapDocumentRefusal.SchemaArrayRequired, "cornerDependencies")]
    [InlineData("array-count", MapDocumentRefusal.WorkCapacityExceeded, "cornerDependencies")]
    [InlineData("base64-type", MapDocumentRefusal.SchemaValueRequired, "heights")]
    [InlineData("base64-length", MapDocumentRefusal.SchemaArrayLengthMismatch, "heights")]
    [InlineData("base64-malformed", MapDocumentRefusal.SchemaBase64Malformed, "heights")]
    [InlineData("base64-decoded-length", MapDocumentRefusal.SchemaArrayLengthMismatch, "heights")]
    public void Decode_schema_guards_identify_the_first_failed_rule(string fault, MapDocumentRefusal reason, string? field)
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Row(1);
        byte[] valid = MapSurfacePatchCodec.Encode(patch);
        JsonObject json = JsonNode.Parse(valid)!.AsObject();
        switch (fault)
        {
            case "unknown": json["extra"] = 1; break;
            case "missing": json.Remove("width"); break;
            case "array": json["cornerDependencies"] = 0; break;
            case "array-count":
                json["cornerDependencies"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject()).ToArray());
                break;
            case "base64-type": json["heights"] = 0; break;
            case "base64-length": json["heights"] = ""; break;
            case "base64-malformed": json["heights"] = new string('!', 24); break;
            // Four corners need 16 bytes. A valid 15-byte token plus four whitespace characters still has length 24.
            case "base64-decoded-length": json["heights"] = Convert.ToBase64String(new byte[15]) + "    "; break;
        }
        string text = json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"width\":1", "\"width\":1,\"width\":1", StringComparison.Ordinal);
        if (fault == "object") text = "[]";
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(text), patch.Key));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(field, ex.FieldPath);
        if (fault == "base64-type" || fault.StartsWith("base64-", StringComparison.Ordinal)) Assert.Equal(16L, ex.ExpectedByteCount);
        if (fault == "base64-length")
        {
            Assert.Equal(0L, ex.ActualCount);
            Assert.Equal(24L, ex.ExpectedCount);
            Assert.Null(ex.ActualByteCount);
        }
        if (fault == "base64-decoded-length") Assert.Equal(15L, ex.ActualByteCount);
        if (fault == "array-count")
        {
            Assert.Equal(5L, ex.ActualCount);
            Assert.Equal(4L, ex.Limit);
            Assert.Equal("cornerDependencies", ex.Budget);
            // Four independently valid corners are the exact array limit.
            json["cornerDependencies"] = new JsonArray(Enumerable.Range(0, 4).Select(i => (JsonNode?)JsonNode.Parse(
                $"{{\"cornerX\":{i % 2},\"cornerZ\":{i / 2},\"owner\":{{\"patch\":{{\"surfaceId\":\"owner\",\"slotX\":\"0\",\"slotZ\":\"0\"}},\"address\":{{\"x\":\"0\",\"z\":\"0\",\"d\":1}}}}}}" )).ToArray());
            Assert.Equal(4, Decode(json, patch.Key).CornerDependencies.Count);
        }
        Assert.Equal(patch.Key, MapSurfacePatchCodec.Decode(valid, patch.Key).Key);
    }

    [Theory]
    [InlineData("01", "1")]
    [InlineData("9223372036854775808", "9223372036854775807")]
    [InlineData("-9223372036854775809", "-9223372036854775808")]
    public void Decimal_identity_preserves_the_submitted_text_and_signed_limits(string refused, string accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        JsonObject json = Payload(patch);
        json["key"]!["slotX"] = refused;
        var ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        Assert.Equal(MapDocumentRefusal.SchemaInt64Malformed, ex.Reason);
        Assert.Equal("slotX", ex.FieldPath);
        Assert.Equal(refused, ex.ActualText);
        Assert.Equal(long.MinValue, ex.MinimumInteger);
        Assert.Equal(long.MaxValue, ex.MaximumInteger);
        json["key"]!["slotX"] = accepted;
        var key = patch.Key with { SlotX = long.Parse(accepted, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal(key, Decode(json, key).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Patch_key_requires_surface_text_in_both_directions(bool decode)
    {
        var patch = SurfacePatchFixtures.Row(1);
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["key"]!["surfaceId"] = " ";
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records.Add(new MapHorizontalOpening("opening", new(" ", 0, 0), new[] { 0 }));
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal("surfaceId", ex.FieldPath);
        Assert.Equal(" ", ex.ActualText);
        json["key"]!["surfaceId"] = patch.Key.SurfaceId;
        patch.Records.Clear();
        Assert.Equal(patch.Key, decode ? Decode(json, patch.Key).Key : RoundTrip(patch).Key);
    }

    [Fact]
    public void Duplicate_decoded_record_retains_its_identity_and_index()
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(new MapHorizontalOpening("opening", patch.Key, new[] { 0 }));
        JsonObject json = Payload(patch);
        json["records"]!.AsArray().Add(json["records"]![0]!.DeepClone());
        var ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, ex.Reason);
        Assert.Equal("opening", ex.RecordId);
        Assert.Equal(1, ex.Index);
        Assert.Equal("id", ex.FieldPath);
        json["records"]!.AsArray().RemoveAt(1);
        Assert.Equal("opening", Assert.Single(Decode(json, patch.Key).Records).Id);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("number")]
    public void Malformed_payload_wrapper_retains_the_requested_patch(string fault)
    {
        var patch = SurfacePatchFixtures.Row(1);
        JsonObject json = Payload(patch);
        json["width"] = "one";
        byte[] bytes = fault == "json" ? Encoding.UTF8.GetBytes("{") : Encoding.UTF8.GetBytes(json.ToJsonString());
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(bytes, patch.Key));
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal(patch.Key, RoundTrip(patch).Key);
    }

    [Fact]
    public void Decode_local_adapter_preserves_the_ordered_findings_and_context()
    {
        var patch = SurfacePatchFixtures.Row(1);
        JsonObject json = Payload(patch);
        json["edgeSubdivisions"] = JsonNode.Parse("[{\"cellX\":0,\"cellZ\":0,\"edge\":0,\"segments\":1}]");
        var ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal("EdgeSubdivisions[0].Segments", ex.FieldPath);
        Assert.Equal(1L, ex.ActualInteger);
        Assert.Equal(2L, ex.MinimumInteger);
        Assert.Equal(64L, ex.MaximumInteger);
        Assert.NotNull(ex.Findings);
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, Assert.Single(ex.Findings).Reason);
        json["edgeSubdivisions"]![0]!["segments"] = 64;
        Assert.Equal(64, Assert.Single(Decode(json, patch.Key).EdgeSubdivisions).Segments);
    }

    [Theory]
    [InlineData("record-text")]
    [InlineData("key-text")]
    [InlineData("record-count")]
    public void Encode_preflight_capacity_retains_only_counts_it_knows(string fault)
    {
        var patch = SurfacePatchFixtures.Row(1);
        string tooLong = new('x', MapSurfacePatchCodec.MaxEncodedBytes + 1);
        if (fault == "record-text") patch.Records.Add(new MapHorizontalOpening(tooLong, patch.Key, new[] { 0 }));
        else if (fault == "key-text") patch.Records.Add(new MapHorizontalOpening("opening", new(tooLong, 0, 0), new[] { 0 }));
        else
            for (int i = 0; i <= MapSurfacePatchCodec.MaxEncodedBytes / 24; i++)
                patch.Records.Add(new MapHorizontalOpening($"o{i}", patch.Key, new[] { 0 }));
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, ex.Reason);
        Assert.Equal("SurfacePatchEncodedBytes", ex.Budget);
        Assert.Equal((long)MapSurfacePatchCodec.MaxEncodedBytes, ex.Limit);
        Assert.Equal(fault == "record-count" ? MapSurfacePatchCodec.MaxEncodedBytes / 24 + 1L : tooLong.Length, ex.ActualCount);
        Assert.Null(ex.ActualByteCount);
        patch.Records.Clear();
        Assert.Empty(RoundTrip(patch).Records);
    }

    [Fact]
    public void Encoded_byte_budget_admits_the_exact_cap_and_refuses_one_more_byte()
    {
        MapSurfacePatch patch = SurfacePatchFixtures.PayloadAtByteCap();
        string id = patch.Records[0].Id;
        patch.Records[0] = patch.Records[0] with { Id = id + "x" };
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, ex.Reason);
        Assert.Equal("SurfacePatchEncodedBytes", ex.Budget);
        Assert.Equal(1_048_576L, ex.Limit);
        Assert.Equal(1_048_577L, ex.ActualByteCount);
        patch.Records[0] = patch.Records[0] with { Id = id };
        byte[] bytes = MapSurfacePatchCodec.Encode(patch);
        Assert.Equal(MapSurfacePatchCodec.MaxEncodedBytes, bytes.Length);
        Assert.Equal(id, Assert.Single(MapSurfacePatchCodec.Decode(bytes, patch.Key).Records).Id);
    }

    static JsonObject Payload(MapSurfacePatch patch) => JsonNode.Parse(MapSurfacePatchCodec.Encode(patch))!.AsObject();
    static MapSurfacePatch Decode(JsonObject json, MapPatchKey key) => MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), key);
    static MapSurfacePatch RoundTrip(MapSurfacePatch patch) => MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(patch), patch.Key);
}
