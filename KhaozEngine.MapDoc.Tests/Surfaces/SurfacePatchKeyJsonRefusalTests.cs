using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfacePatchKeyJsonRefusalTests
{
    static JsonSerializerOptions Options() => new() { Converters = { new MapPatchKeyJsonConverter() } };
    const string Valid = "{\"surfaceId\":\"s\",\"slotX\":\"0\",\"slotZ\":\"0\"}";

    [Theory]
    [InlineData("[]", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("{\"surfaceId\":1,\"slotX\":\"0\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.surfaceId")]
    [InlineData("{\"surfaceId\":\"s\",\"SurfaceId\":\"other\",\"slotX\":\"0\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaMemberDuplicate, "$.SurfaceId")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":\"0\",\"SlotX\":\"0\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaMemberDuplicate, "$.SlotX")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":\"0\",\"slotZ\":\"0\",\"SlotZ\":\"0\"}", MapDocumentRefusal.SchemaMemberDuplicate, "$.SlotZ")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":\"0\",\"slotZ\":\"0\",\"extra\":1}", MapDocumentRefusal.SchemaMemberUnknown, "$.extra")]
    [InlineData("{\"slotX\":\"0\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.surfaceId")]
    [InlineData("{\"surfaceId\":\" \",\"slotX\":\"0\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.surfaceId")]
    [InlineData("{\"surfaceId\":\"s\",\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.slotX")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.slotZ")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":0,\"slotZ\":\"0\"}", MapDocumentRefusal.SchemaValueRequired, "$.slotX")]
    [InlineData("{\"surfaceId\":\"s\",\"slotX\":\"0\",\"slotZ\":null}", MapDocumentRefusal.SchemaValueRequired, "$.slotZ")]
    public void Key_schema_refusals_keep_the_first_failed_field(string json, MapDocumentRefusal reason, string field)
    {
        var error = Assert.Throws<MapSurfaceJsonException>(() => JsonSerializer.Deserialize<MapPatchKey>(json, Options()));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "patch", "invalid patch key");
        Assert.Equal(reason, wrapped.Reason);
        Assert.Equal(field, wrapped.FieldPath);
        Assert.Equal(error.SurfaceId, wrapped.SurfaceId);
        Assert.Equal(new MapPatchKey("s", 0, 0), JsonSerializer.Deserialize<MapPatchKey>(Valid, Options()));
    }

    [Theory]
    [InlineData("+1", "1", 1L)]
    [InlineData("01", "1", 1L)]
    [InlineData("-0", "0", 0L)]
    [InlineData("9223372036854775808", "9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775809", "-9223372036854775808", long.MinValue)]
    public void Slot_text_keeps_the_submitted_representation(string refused, string accepted, long expected)
    {
        string Json(string text) => $"{{\"surfaceId\":\"s\",\"slotX\":\"{text}\",\"slotZ\":\"0\"}}";
        var error = Assert.Throws<MapSurfaceJsonException>(() => JsonSerializer.Deserialize<MapPatchKey>(Json(refused), Options()));
        Assert.Equal(MapDocumentRefusal.SchemaInt64Malformed, error.Reason);
        Assert.Equal("$.slotX", error.FieldPath);
        Assert.Equal("s", error.SurfaceId);
        Assert.Equal(refused, error.ActualText);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "patch", "invalid slot");
        Assert.Equal(MapDocumentRefusal.SchemaInt64Malformed, wrapped.Reason);
        Assert.Equal(refused, wrapped.ActualText);
        Assert.Equal("s", wrapped.SurfaceId);
        Assert.Equal(new MapPatchKey("s", expected, 0), JsonSerializer.Deserialize<MapPatchKey>(Json(accepted), Options()));
    }

    [Fact]
    public void Key_writer_keeps_the_JSON_catch_contract_for_a_missing_surface()
    {
        var error = Assert.Throws<MapSurfaceJsonException>(() => JsonSerializer.Serialize(new MapPatchKey("", 0, 0), Options()));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$.surfaceId", error.FieldPath);
        Assert.Equal("", error.SurfaceId);
        Assert.Equal(new MapPatchKey("s", 0, 0), JsonSerializer.Deserialize<MapPatchKey>(
            JsonSerializer.Serialize(new MapPatchKey("s", 0, 0), Options()), Options()));
    }
}
