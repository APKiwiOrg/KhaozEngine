using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabBindingJsonRefusalTests
{
    const string Surface = """{"kind":"Surface","surfaceId":"floor"}""";
    static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapPrefabJson.MarkerBindingConverter());
        return options;
    }

    [Theory]
    [InlineData("[]", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("{}", MapDocumentRefusal.SchemaMemberMissing, "$.kind")]
    [InlineData("{\"kind\":0}", MapDocumentRefusal.SchemaValueRequired, "$.kind")]
    [InlineData("{\"kind\":\"Future\"}", MapDocumentRefusal.SchemaEnumUnsupported, "$.kind")]
    [InlineData("{\"kind\":\"Surface\",\"surfaceId\":\"floor\",\"extra\":0}", MapDocumentRefusal.SchemaMemberUnknown, "$.extra")]
    [InlineData("{\"kind\":\"Surface\",\"surfaceId\":\"floor\",\"surfaceId\":\"floor\"}", MapDocumentRefusal.SchemaMemberDuplicate, "$.surfaceId")]
    [InlineData("{\"kind\":\"Surface\",\"surfaceId\":null}", MapDocumentRefusal.SchemaMemberNull, "$.surfaceId")]
    [InlineData("{\"kind\":\"Surface\"}", MapDocumentRefusal.SchemaMemberMissing, "$.surfaceId")]
    [InlineData("{\"kind\":\"Surface\",\"surfaceId\":0}", MapDocumentRefusal.SchemaValueRequired, "$.surfaceId")]
    public void BindingReader_ReportsTheFirstFailedMemberAndAcceptsItsRepair(string json, MapDocumentRefusal reason, string field)
    {
        var error = Assert.Throws<MapPrefabJsonException>(() => JsonSerializer.Deserialize<MapSupportBinding>(json, Options()));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (reason == MapDocumentRefusal.SchemaEnumUnsupported)
        {
            Assert.Equal(typeof(MapSupportBindingKind), error.EnumType);
            Assert.Equal("Future", error.ActualText);
        }
        Assert.Equal("floor", JsonSerializer.Deserialize<MapSupportBinding>(Surface, Options())!.SurfaceId);
    }

    [Fact]
    public void JsonWrapper_PreservesTheConcreteBindingRefusalAndReaderContext()
    {
        var error = Assert.Throws<MapPrefabJsonException>(() =>
            JsonSerializer.Deserialize<MapSupportBinding>("{\"kind\":\"Future\"}", Options()));
        var wrapped = MapDocumentJsonErrors.Wrap(error, "binding-source", "Binding refused", new(2, 3), "binding.json", "binding-resource");
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, wrapped.Reason);
        Assert.Equal(error.FieldPath, wrapped.FieldPath);
        Assert.Equal(typeof(MapSupportBindingKind), wrapped.EnumType);
        Assert.Equal("Future", wrapped.ActualText);
        Assert.Equal("binding-source", wrapped.SourceName);
        Assert.Equal("binding.json", wrapped.Path);
        Assert.Equal("binding-resource", wrapped.ResourceId);
        Assert.Equal(new MapTileCoord(2, 3), wrapped.Tile);
    }

    [Theory]
    [InlineData("\"bad\"", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("1e100", MapDocumentRefusal.ValueNonfinite)]
    public void BindingNumbers_DistinguishWrongTypesFromNonfiniteValues(string number, MapDocumentRefusal reason)
    {
        string Json(string value) => "{\"kind\":\"Space\",\"spaceId\":\"room\",\"referenceY\":" + value + ",\"searchBelow\":0,\"searchAbove\":0}";
        var error = Assert.Throws<MapPrefabJsonException>(() => JsonSerializer.Deserialize<MapSupportBinding>(Json(number), Options()));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("$.referenceY", error.FieldPath);
        if (reason == MapDocumentRefusal.ValueNonfinite) Assert.Equal(double.PositiveInfinity, error.ActualNumber);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "source", "Invalid binding");
        Assert.Equal(reason, wrapped.Reason);
        Assert.Equal(error.ActualNumber, wrapped.ActualNumber);
        Assert.Equal(float.MaxValue, JsonSerializer.Deserialize<MapSupportBinding>(Json("3.4028235e38"), Options())!.ReferenceY);
    }

    [Theory]
    [InlineData("surface-missing", MapDocumentRefusal.SchemaValueRequired, "$.surfaceId")]
    [InlineData("surface-foreign", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.spaceId")]
    [InlineData("space-missing", MapDocumentRefusal.SchemaValueRequired, "$.spaceId")]
    [InlineData("space-foreign", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.surfaceId")]
    [InlineData("space-range", MapDocumentRefusal.SchemaValueRequired, "$.searchAbove")]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "$.kind")]
    public void BindingWriter_PreservesItsShapeAdmissionAndWritesNothingOnRefusal(string fault, MapDocumentRefusal reason, string field)
    {
        var surface = new MapSupportBinding(MapSupportBindingKind.Surface, "floor", null, null, null, null);
        var space = new MapSupportBinding(MapSupportBindingKind.Space, null, "room", 0, 0, 0);
        var invalid = fault switch
        {
            "surface-missing" => surface with { SurfaceId = null },
            "surface-foreign" => surface with { SpaceId = "room" },
            "space-missing" => space with { SpaceId = null },
            "space-foreign" => space with { SurfaceId = "floor" },
            "space-range" => space with { SearchAbove = null },
            _ => surface with { Kind = (MapSupportBindingKind)9 },
        };
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        byte[] before = stream.ToArray();
        var converter = new MapPrefabJson.MarkerBindingConverter();
        var error = Assert.Throws<MapPrefabJsonException>(() => converter.Write(writer, invalid, Options()));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(invalid.SurfaceId, error.SurfaceId);
        Assert.Equal(invalid.SpaceId, error.SpaceId);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapSupportBindingKind), error.EnumType);
            Assert.Equal(9L, error.ActualInteger);
        }
        writer.Flush();
        Assert.Equal(before, stream.ToArray());
        converter.Write(writer, fault.StartsWith("space", StringComparison.Ordinal) ? space : surface, Options());
        writer.Flush();
        using var accepted = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(fault.StartsWith("space", StringComparison.Ordinal) ? "Space" : "Surface", accepted.RootElement.GetProperty("kind").GetString());
    }
}
