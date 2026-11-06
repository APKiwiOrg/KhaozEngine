using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceRationalJsonRefusalTests
{
    static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MapRationalJsonConverter());
        return options;
    }

    [Theory]
    [InlineData("[]", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("{\"numerator\":\"1\",\"denominator\":1}", MapDocumentRefusal.SchemaValueRequired, "$.numerator")]
    [InlineData("{\"numerator\":1,\"denominator\":2147483648}", MapDocumentRefusal.SchemaValueRequired, "$.denominator")]
    [InlineData("{\"numerator\":1,\"Numerator\":1,\"denominator\":1}", MapDocumentRefusal.SchemaMemberDuplicate, "$.Numerator")]
    [InlineData("{\"numerator\":1,\"denominator\":1,\"Denominator\":1}", MapDocumentRefusal.SchemaMemberDuplicate, "$.Denominator")]
    [InlineData("{\"numerator\":1,\"denominator\":1,\"extra\":1}", MapDocumentRefusal.SchemaMemberUnknown, "$.extra")]
    [InlineData("{\"denominator\":1}", MapDocumentRefusal.SchemaMemberMissing, "$.numerator")]
    [InlineData("{\"numerator\":1}", MapDocumentRefusal.SchemaMemberMissing, "$.denominator")]
    public void Unit_schema_refusals_keep_the_actual_field(string json, MapDocumentRefusal reason, string field)
    {
        var error = Assert.Throws<MapSurfaceJsonException>(() => JsonSerializer.Deserialize<MapRational>(json, Options()));
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "surface.json", "invalid unit");
        Assert.Equal(reason, wrapped.Reason);
        Assert.Equal(field, wrapped.FieldPath);
        Assert.Equal("surface.json", wrapped.SourceName);
        Assert.Equal(new MapRational(1, 1), JsonSerializer.Deserialize<MapRational>("{\"numerator\":1,\"denominator\":1}", Options()));
    }

    [Theory]
    [InlineData(0, 1, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(1, 0, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(2, 2, MapDocumentRefusal.SurfaceUnitUnreduced)]
    public void Unit_constructor_wrapping_preserves_the_lower_rule(int numerator, int denominator, MapDocumentRefusal reason)
    {
        string json = $"{{\"numerator\":{numerator},\"denominator\":{denominator}}}";
        var error = Assert.Throws<MapSurfaceJsonException>(() => JsonSerializer.Deserialize<MapRational>(json, Options()));
        Assert.Equal(reason, error.Reason);
        var wrapped = MapDocumentJsonErrors.Wrap(error, "unit", "invalid unit");
        Assert.Equal(reason, wrapped.Reason);
        if (reason == MapDocumentRefusal.SurfaceUnitUnreduced)
        {
            Assert.Equal(2, error.Numerator);
            Assert.Equal(2, error.Denominator);
            Assert.Equal(2, wrapped.Numerator);
            Assert.Equal(2, wrapped.Denominator);
        }
        else
        {
            Assert.Equal(numerator == 0 ? "numerator" : "denominator", error.FieldPath);
            Assert.Equal(0L, error.ActualInteger);
            Assert.Equal(1L, error.MinimumInteger);
            Assert.Equal(error.FieldPath, wrapped.FieldPath);
            Assert.Equal(0L, wrapped.ActualInteger);
        }
        Assert.Equal(new MapRational(1, 1), JsonSerializer.Deserialize<MapRational>("{\"numerator\":1,\"denominator\":1}", Options()));
    }
}
