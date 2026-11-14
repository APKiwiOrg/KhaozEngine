using System.Collections.Generic;
using System.IO;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The catalog half of the steep companion: the optional <c>steep</c> object of a ground material row,
/// its defaults, every refusal the loader makes, and the validator and digest that see it afterwards.</summary>
public class GroundMaterialSteepTests
{
    // Grass with a companion, rock to be it, and water to be refused as one.
    static string Catalog(string steep, string extra = "") => $$"""
        { "materials": [
            { "id": 1, "name": "grass", "color": "#4d8a3a"{{(steep.Length == 0 ? "" : ", \"steep\": " + steep)}} },
            { "id": 4, "name": "water", "color": "#2a5a9a", "kind": "Water" },
            { "id": 14, "name": "rock", "color": "#7a7a7a" }{{extra}} ] }
        """;

    static TileWorldException Refused(string steep) =>
        Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(Catalog(steep), "steep.json"));

    [Fact]
    public void A_full_steep_object_round_trips()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            Catalog("""{ "material": 14, "fromDegrees": 30, "toDegrees": 70.5 }"""), "steep.json");

        GroundMaterialSteep steep = c.Material(1)!.Steep!;
        Assert.Equal((ushort)14, steep.Material);
        Assert.Equal(30f, steep.FromDegrees);
        Assert.Equal(70.5f, steep.ToDegrees);
        Assert.Equal(30f, steep.ResolvedFromDegrees);
        Assert.Equal(70.5f, steep.ResolvedToDegrees);
        Assert.Null(c.Material(14)!.Steep);
    }

    [Fact]
    public void An_omitted_band_takes_the_engine_defaults()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");

        GroundMaterialSteep steep = c.Material(1)!.Steep!;
        Assert.Null(steep.FromDegrees);
        Assert.Null(steep.ToDegrees);
        Assert.Equal(GroundMaterialSteep.DefaultFromDegrees, steep.ResolvedFromDegrees);
        Assert.Equal(GroundMaterialSteep.DefaultToDegrees, steep.ResolvedToDegrees);
        Assert.Equal(40f, GroundMaterialSteep.DefaultFromDegrees);
        Assert.Equal(55f, GroundMaterialSteep.DefaultToDegrees);
    }

    [Fact]
    public void One_end_of_the_band_can_be_authored_alone()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14, "toDegrees": 80 }"""), "steep.json");

        GroundMaterialSteep steep = c.Material(1)!.Steep!;
        Assert.Equal(GroundMaterialSteep.DefaultFromDegrees, steep.ResolvedFromDegrees);
        Assert.Equal(80f, steep.ResolvedToDegrees);
    }

    [Theory]
    [InlineData("{ \"material\": 99 }", TileWorldRefusal.SteepCompanionMissing, 99, null)]
    [InlineData("{ \"material\": 1 }", TileWorldRefusal.SteepSelfReference, 1, null)]
    [InlineData("{ \"material\": 4 }", TileWorldRefusal.SteepCompanionWater, 4, "water")]
    public void An_invalid_companion_is_refused_by_name(
        string steep, TileWorldRefusal reason, ushort companionId, string? companionName)
    {
        TileWorldException ex = Refused(steep);

        Assert.Equal(reason, ex.Reason);
        Assert.Equal("steep.json", ex.SourceName);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal(companionId, ex.CompanionMaterialId);
        Assert.Equal(companionName, ex.CompanionMaterialName);
        Assert.Null(ex.FromDegrees);
        Assert.Null(ex.ToDegrees);

        TileWorldCatalogs sound = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");
        Assert.Null(GroundMaterialSteepRules.Problem(sound.Material(1)!, sound));
    }

    [Theory]
    [InlineData("""{ "material": 14, "fromDegrees": 50, "toDegrees": 50 }""", 50f, 50f, false, false)]
    [InlineData("""{ "material": 14, "fromDegrees": 60, "toDegrees": 45 }""", 60f, 45f, false, false)]
    [InlineData("""{ "material": 14, "fromDegrees": 60 }""", 60f, 55f, false, true)]
    [InlineData("""{ "material": 14, "toDegrees": 20 }""", 40f, 20f, true, false)]
    public void A_band_that_does_not_rise_is_refused(
        string steep, float from, float to, bool fromIsDefault, bool toIsDefault)
    {
        TileWorldException ex = Refused(steep);
        Assert.Equal(TileWorldRefusal.SteepBandUnordered, ex.Reason);
        Assert.Equal("steep.json", ex.SourceName);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal(from, ex.FromDegrees);
        Assert.Equal(to, ex.ToDegrees);
        Assert.Equal(fromIsDefault, ex.FromIsDefault);
        Assert.Equal(toIsDefault, ex.ToIsDefault);

        TileWorldCatalogs sound = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");
        GroundMaterial grass = sound.Material(1)!;
        grass.Steep!.FromDegrees = from;
        grass.Steep.ToDegrees = from + 5f;
        GroundMaterialSteepRules.ThrowIfInvalid(sound, "steep.json");
        Assert.Null(GroundMaterialSteepRules.Problem(grass, sound));
    }

    [Fact]
    public void A_band_whose_ends_share_a_cosine_is_refused_at_load()
    {
        // Exact normal powers of two give radians below 2^-20. Each cosine's deficit is under 2^-41,
        // far below the 2^-25 rounding midpoint, so both round to one without cancellation or subnormals.
        const float from = 1f / 65536f, to = 1f / 32768f;
        TileWorldException ex = Refused(
            """{ "material": 14, "fromDegrees": 0.0000152587890625, "toDegrees": 0.000030517578125 }""");
        Assert.Equal(TileWorldRefusal.SteepCosinesCollapsed, ex.Reason);
        Assert.Equal("steep.json", ex.SourceName);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal(from, ex.FromDegrees);
        Assert.Equal(to, ex.ToDegrees);
    }

    [Fact]
    public void A_narrow_band_that_still_resolves_loads()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            Catalog("""{ "material": 14, "fromDegrees": 40, "toDegrees": 40.5 }"""), "steep.json");
        Assert.Equal(40.5f, c.Material(1)!.Steep!.ResolvedToDegrees);
    }

    // The schema refuses these before the semantic rules see them, retaining the instance location.
    [Theory]
    [InlineData("""{ "material": 14, "fromDegrees": 0 }""", "/materials/0/steep/fromDegrees", "fromDegrees")]
    [InlineData("""{ "material": 14, "fromDegrees": -5 }""", "/materials/0/steep/fromDegrees", "fromDegrees")]
    [InlineData("""{ "material": 14, "toDegrees": 90 }""", "/materials/0/steep/toDegrees", "toDegrees")]
    [InlineData("""{ "material": 14, "toDegrees": 120 }""", "/materials/0/steep/toDegrees", "toDegrees")]
    [InlineData("""{ "fromDegrees": 40 }""", "/materials/0/steep", "steep")]
    [InlineData("""{ "material": 0 }""", "/materials/0/steep/material", "material")]
    [InlineData("""{ "material": 14, "chain": true }""", "/materials/0/steep/chain", "chain")]
    public void The_schema_refuses_a_malformed_steep_object(string steep, string path, string property)
    {
        TileWorldException ex = Refused(steep);
        Assert.Equal(TileWorldRefusal.CatalogSchemaMismatch, ex.Reason);
        Assert.Equal("steep.json", ex.SourceName);
        Assert.NotNull(ex.SchemaFields);
        Assert.Contains(path, ex.SchemaFields);
        Assert.NotNull(ex.SchemaFindings);
        Assert.Contains(ex.SchemaFindings, finding => finding.InstanceLocation == path &&
            finding.PropertyName == property);
    }

    // A catalog built in code never meets the schema, so the rules carry the range check too.
    [Theory]
    [InlineData(0f, 50f, TileWorldRefusal.SteepFromOutsideRightAngle, "fromDegrees")]
    [InlineData(90f, 50f, TileWorldRefusal.SteepFromOutsideRightAngle, "fromDegrees")]
    [InlineData(float.NaN, 50f, TileWorldRefusal.SteepFromOutsideRightAngle, "fromDegrees")]
    [InlineData(40f, 0f, TileWorldRefusal.SteepToOutsideRightAngle, "toDegrees")]
    [InlineData(40f, 90f, TileWorldRefusal.SteepToOutsideRightAngle, "toDegrees")]
    [InlineData(40f, float.NaN, TileWorldRefusal.SteepToOutsideRightAngle, "toDegrees")]
    public void The_rules_refuse_a_band_outside_a_right_angle(
        float from, float to, TileWorldRefusal reason, string field)
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog(""), "steep.json");
        GroundMaterial grass = c.Material(1)!;
        grass.Steep = new GroundMaterialSteep { Material = 14, FromDegrees = from, ToDegrees = to };

        Assert.IsType<string>(GroundMaterialSteepRules.Problem(grass, c));
        TileWorldException ex = Assert.Throws<TileWorldException>(() =>
            GroundMaterialSteepRules.ThrowIfInvalid(c, "steep.json"));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("steep.json", ex.SourceName);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(from, ex.FromDegrees);
        Assert.Equal(to, ex.ToDegrees);
        Assert.Null(ex.CompanionMaterialId);

        grass.Steep.FromDegrees = 40f;
        grass.Steep.ToDegrees = 50f;
        GroundMaterialSteepRules.ThrowIfInvalid(c, "steep.json");
        Assert.Null(GroundMaterialSteepRules.Problem(grass, c));
    }

    [Fact]
    public void A_companions_own_companion_is_not_followed_or_refused()
    {
        // grass -> rock -> dirt is two independent one-hop rules. Nothing about rock's companion is grass's.
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            Catalog("""{ "material": 14 }""", """, { "id": 2, "name": "dirt", "color": "#8a6a3a" }""")
                .Replace("\"name\": \"rock\", \"color\": \"#7a7a7a\"", "\"name\": \"rock\", \"color\": \"#7a7a7a\", \"steep\": { \"material\": 2 }"),
            "chain.json");

        Assert.Equal((ushort)14, c.Material(1)!.Steep!.Material);
        Assert.Equal((ushort)2, c.Material(14)!.Steep!.Material);
    }

    [Fact]
    public void A_companion_must_be_in_the_same_catalog_file()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Sub("grass.json"),
            """{ "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a", "steep": { "material": 14 } } ] }""");
        File.WriteAllText(tmp.Sub("rock.json"), """{ "materials": [ { "id": 14, "name": "rock", "color": "#7a7a7a" } ] }""");

        var ex = Assert.Throws<TileWorldException>(() =>
            TileWorldCatalogs.Load(new[] { tmp.Sub("grass.json"), tmp.Sub("rock.json") }));
        Assert.Equal(TileWorldRefusal.SteepCompanionMissing, ex.Reason);
        Assert.Equal(tmp.Sub("grass.json"), ex.SourceName);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal((ushort)14, ex.CompanionMaterialId);

        File.WriteAllText(tmp.Sub("grass.json"), Catalog("""{ "material": 14 }"""));
        TileWorldCatalogs sound = TileWorldCatalogs.Load(new[] { tmp.Sub("grass.json") });
        Assert.Null(GroundMaterialSteepRules.Problem(sound.Material(1)!, sound));
    }

    [Fact]
    public void The_validator_reports_a_companion_edited_wrong_in_memory()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");
        c.Material(1)!.Steep!.Material = 4;

        IReadOnlyList<TileWorldIssue> issues = TileWorldValidator.Validate(new TileWorldDocument(), c);

        TileWorldIssue issue = Assert.Single(issues, i => i.Code == "material.steep");
        Assert.Contains("Water", issue.Message);
    }

    [Fact]
    public void A_sound_companion_raises_no_validator_issue()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");
        Assert.DoesNotContain(TileWorldValidator.Validate(new TileWorldDocument(), c), i => i.Code == "material.steep");
    }

    [Fact]
    public void A_build_snapshot_owns_its_copy_of_the_companion()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            Catalog("""{ "material": 14, "fromDegrees": 30 }"""), "steep.json");
        GroundMaterial source = c.Material(1)!;
        source.NormalTexture = "n.png";
        source.RoughnessTexture = "r.png";
        source.HeightTexture = "h.png";
        source.HeightBlendSharpness = 16;
        source.Feather = new() { WidthMetres = 0.3f, Breakup = 0.7f };
        source.AntiTiling = GroundMaterialAntiTiling.Rotate;
        TileWorldCatalogs copy = c.CopyMaterials();

        c.Material(1)!.Steep!.FromDegrees = 35f;

        GroundMaterialSteep copied = copy.Material(1)!.Steep!;
        Assert.NotSame(c.Material(1)!.Steep, copied);
        Assert.Equal(30f, copied.FromDegrees);
        Assert.Null(copied.ToDegrees);
        Assert.Equal((ushort)14, copied.Material);
        GroundMaterial snapshot = copy.Material(1)!;
        Assert.Equal("n.png", snapshot.NormalTexture);
        Assert.Equal("r.png", snapshot.RoughnessTexture);
        Assert.Equal("h.png", snapshot.HeightTexture);
        Assert.Equal(16f, snapshot.HeightBlendSharpness);
        Assert.Equal(GroundMaterialAntiTiling.Rotate, snapshot.AntiTiling);
        Assert.NotSame(source.Feather, snapshot.Feather);
        source.Feather.WidthMetres = 0.5f;
        source.Feather.Breakup = 0;
        Assert.Equal(0.3f, snapshot.Feather!.WidthMetres);
        Assert.Equal(0.7f, snapshot.Feather.Breakup);
    }

    [Fact]
    public void Every_steep_value_moves_the_catalog_digest()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog(""), "steep.json");
        var seen = new HashSet<string> { TileWorldHash.OfCatalogs(c) };
        GroundMaterial grass = c.Material(1)!;

        grass.Steep = new GroundMaterialSteep { Material = 14 };
        Assert.True(seen.Add(TileWorldHash.OfCatalogs(c)), "adding a companion");
        grass.Steep.FromDegrees = 30f;
        Assert.True(seen.Add(TileWorldHash.OfCatalogs(c)), "fromDegrees");
        grass.Steep.ToDegrees = 60f;
        Assert.True(seen.Add(TileWorldHash.OfCatalogs(c)), "toDegrees");
        grass.Steep.Material = 4;
        Assert.True(seen.Add(TileWorldHash.OfCatalogs(c)), "the companion id");
    }
}
