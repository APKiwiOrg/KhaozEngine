using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class GroundMaterialReliefTests
{
    static TileWorldCatalogs Catalog(string properties = "") => TileWorldCatalogs.LoadJson(
        $$"""{ "materials": [{ "id": 7, "name": "stone", "color": "#123456"{{properties}} }] }""", "relief.json");

    [Fact]
    public void Relief_fields_round_trip_through_catalog_json()
    {
        GroundMaterial material = Catalog("""
            , "normalTexture": "normal.png", "roughnessTexture": "roughness.png", "heightTexture": "height.png",
            "heightBlendSharpness": 16, "feather": { "widthMetres": 0.3, "breakup": 0.7 }, "antiTiling": "Rotate"
            """).Material(7)!;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        string written = JsonSerializer.Serialize(new { materials = new[] { material } }, options);
        GroundMaterial roundTrip = TileWorldCatalogs.LoadJson(written, "round-trip.json").Material(7)!;
        Assert.Equal("normal.png", roundTrip.NormalTexture);
        Assert.Equal("roughness.png", roundTrip.RoughnessTexture);
        Assert.Equal("height.png", roundTrip.HeightTexture);
        Assert.Equal(16f, roundTrip.HeightBlendSharpness);
        Assert.Equal(0.3f, roundTrip.Feather!.WidthMetres);
        Assert.Equal(0.7f, roundTrip.Feather.Breakup);
        Assert.Equal(GroundMaterialAntiTiling.Rotate, roundTrip.AntiTiling);
        GroundMaterial plain = Catalog().Material(7)!;
        Assert.Null(plain.NormalTexture);
        Assert.Null(plain.RoughnessTexture);
        Assert.Null(plain.HeightTexture);
        Assert.Null(plain.HeightBlendSharpness);
        Assert.Null(plain.Feather);
        Assert.Equal(GroundMaterialAntiTiling.None, plain.AntiTiling);
    }

    [Theory]
    [InlineData("\"heightTexture\": \"h.png\", \"heightBlendSharpness\": 0.99", "\"heightTexture\": \"h.png\", \"heightBlendSharpness\": 1")]
    [InlineData("\"heightTexture\": \"h.png\", \"heightBlendSharpness\": 65", "\"heightTexture\": \"h.png\", \"heightBlendSharpness\": 64")]
    [InlineData("\"feather\": { \"widthMetres\": 0 }", "\"feather\": { \"widthMetres\": 0.01 }")]
    [InlineData("\"feather\": { \"breakup\": -0.01 }", "\"feather\": { \"breakup\": 0 }")]
    [InlineData("\"feather\": { \"breakup\": 1.01 }", "\"feather\": { \"breakup\": 1 }")]
    [InlineData("\"feather\": { \"unknown\": 1 }", "\"feather\": {}")]
    [InlineData("\"antiTiling\": \"Unknown\"", "\"antiTiling\": \"None\"")]
    public void Schema_refuses_invalid_relief_values(string invalid, string control)
    {
        TileWorldException ex = Assert.Throws<TileWorldException>(() => Catalog(", " + invalid));
        Assert.Equal(TileWorldRefusal.CatalogSchemaMismatch, ex.Reason);
        Assert.Equal("relief.json", ex.SourceName);
        Assert.NotEmpty(ex.SchemaFields!);
        Assert.Null(GroundMaterialReliefRules.Problem(Catalog(", " + control).Material(7)!));
    }

    [Theory]
    [InlineData("heightBlendSharpness", float.NaN, 1f, TileWorldRefusal.GroundMaterialHeightSharpnessOutsideRange)]
    [InlineData("heightBlendSharpness", float.PositiveInfinity, 64f, TileWorldRefusal.GroundMaterialHeightSharpnessOutsideRange)]
    [InlineData("heightBlendSharpness", 0f, 1f, TileWorldRefusal.GroundMaterialHeightSharpnessOutsideRange)]
    [InlineData("heightBlendSharpness", 65f, 64f, TileWorldRefusal.GroundMaterialHeightSharpnessOutsideRange)]
    [InlineData("feather.widthMetres", float.NaN, 0.01f, TileWorldRefusal.GroundMaterialFeatherWidthInvalid)]
    [InlineData("feather.widthMetres", float.PositiveInfinity, 0.01f, TileWorldRefusal.GroundMaterialFeatherWidthInvalid)]
    [InlineData("feather.widthMetres", 0f, 0.01f, TileWorldRefusal.GroundMaterialFeatherWidthInvalid)]
    [InlineData("feather.widthMetres", -1f, 0.01f, TileWorldRefusal.GroundMaterialFeatherWidthInvalid)]
    [InlineData("feather.breakup", float.NaN, 0f, TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange)]
    [InlineData("feather.breakup", float.PositiveInfinity, 1f, TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange)]
    [InlineData("feather.breakup", -0.01f, 0f, TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange)]
    [InlineData("feather.breakup", 1.01f, 1f, TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange)]
    [InlineData("antiTiling", 3f, 2f, TileWorldRefusal.GroundMaterialAntiTilingUndefined)]
    public void In_memory_refusals_name_the_material_field_and_value(
        string field, float invalid, float control, TileWorldRefusal reason)
    {
        TileWorldCatalogs catalogs = Catalog(", \"heightTexture\": \"h.png\", \"feather\": {}");
        GroundMaterial material = catalogs.Material(7)!;
        Set(invalid);
        Assert.NotNull(GroundMaterialReliefRules.Problem(material));
        TileWorldException ex = Assert.Throws<TileWorldException>(() => GroundMaterialReliefRules.ThrowIfInvalid(catalogs, "edited"));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("edited", ex.SourceName);
        Assert.Equal((ushort)7, ex.MaterialId);
        Assert.Equal("stone", ex.MaterialName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(invalid, ex.Value);
        Set(control);
        GroundMaterialReliefRules.ThrowIfInvalid(catalogs, "edited");
        Assert.Null(GroundMaterialReliefRules.Problem(material));

        void Set(float value)
        {
            switch (field)
            {
                case "heightBlendSharpness": material.HeightBlendSharpness = value; break;
                case "feather.widthMetres": material.Feather!.WidthMetres = value; break;
                case "feather.breakup": material.Feather!.Breakup = value; break;
                case "antiTiling": material.AntiTiling = (GroundMaterialAntiTiling)(int)value; break;
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Sharpness_requires_a_height_map_at_load_and_after_edits(string? height)
    {
        TileWorldCatalogs catalogs = Catalog();
        GroundMaterial material = catalogs.Material(7)!;
        material.HeightBlendSharpness = 1;
        material.HeightTexture = height;
        TileWorldException ex = Assert.Throws<TileWorldException>(() => GroundMaterialReliefRules.ThrowIfInvalid(catalogs, "edited"));
        Assert.Equal(TileWorldRefusal.GroundMaterialHeightSharpnessWithoutHeight, ex.Reason);
        Assert.Equal((ushort)7, ex.MaterialId);
        Assert.Equal("heightBlendSharpness", ex.Field);
        Assert.Null(ex.Value);
        TileWorldException loaded = Assert.Throws<TileWorldException>(() => Catalog(", \"heightBlendSharpness\": 1"));
        Assert.Equal(ex.Reason, loaded.Reason);
        material.HeightTexture = "height.png";
        GroundMaterialReliefRules.ThrowIfInvalid(catalogs, "edited");
    }

    [Fact]
    public void Refusals_follow_material_id_order_and_the_validator_reports_each_unsound_material()
    {
        TileWorldCatalogs catalogs = TileWorldCatalogs.LoadJson("""
            { "materials": [ { "id": 9, "name": "late", "color": "#ffffff" },
              { "id": 2, "name": "early", "color": "#ffffff" } ] }
            """, "ordered");
        catalogs.Material(9)!.Feather = new() { Breakup = 2 };
        catalogs.Material(2)!.HeightBlendSharpness = 1;
        TileWorldException ex = Assert.Throws<TileWorldException>(() => GroundMaterialReliefRules.ThrowIfInvalid(catalogs, "ordered"));
        Assert.Equal((ushort)2, ex.MaterialId);
        Assert.Equal(TileWorldRefusal.GroundMaterialHeightSharpnessWithoutHeight, ex.Reason);
        Assert.Equal(2, System.Linq.Enumerable.Count(TileWorldValidator.Validate(new(), catalogs), i => i.Code == "material.relief"));
        catalogs.Material(2)!.HeightBlendSharpness = null;
        catalogs.Material(9)!.Feather!.Breakup = 1;
        Assert.DoesNotContain(TileWorldValidator.Validate(new(), catalogs), i => i.Code == "material.relief");
    }

    [Theory]
    [InlineData("", ", \"normalTexture\": \"normal.png\"")]
    [InlineData("", ", \"roughnessTexture\": \"roughness.png\"")]
    [InlineData("", ", \"heightTexture\": \"height.png\"")]
    [InlineData(", \"heightTexture\": \"height.png\"", ", \"heightTexture\": \"height.png\", \"heightBlendSharpness\": 16")]
    [InlineData("", ", \"feather\": {}")]
    [InlineData(", \"feather\": {}", ", \"feather\": { \"widthMetres\": 0.25 }")]
    [InlineData(", \"feather\": {}", ", \"feather\": { \"breakup\": 0.75 }")]
    [InlineData("", ", \"antiTiling\": \"Offset\"")]
    [InlineData(", \"antiTiling\": \"Offset\"", ", \"antiTiling\": \"Rotate\"")]
    public void Authored_relief_fields_participate_in_catalog_identity(string before, string after)
    {
        Assert.NotEqual(TileWorldHash.OfCatalogs(Catalog(before)), TileWorldHash.OfCatalogs(Catalog(after)));
    }
}
