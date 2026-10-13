using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [Fact]
    public void An_unknown_companion_is_refused_by_name()
    {
        TileWorldException ex = Refused("""{ "material": 99 }""");
        Assert.Contains("steep.json", ex.Message);
        Assert.Contains("material 1 ('grass') steep names material 99, which is not in the same catalog", ex.Message);
    }

    [Fact]
    public void A_self_reference_is_refused()
    {
        TileWorldException ex = Refused("""{ "material": 1 }""");
        Assert.Contains("material 1 ('grass') steep names the material itself", ex.Message);
    }

    [Fact]
    public void A_water_companion_is_refused()
    {
        TileWorldException ex = Refused("""{ "material": 4 }""");
        Assert.Contains("names material 4 ('water'), which is Water", ex.Message);
    }

    [Theory]
    [InlineData("""{ "material": 14, "fromDegrees": 50, "toDegrees": 50 }""", "fromDegrees 50 is not below toDegrees 50.")]
    [InlineData("""{ "material": 14, "fromDegrees": 60, "toDegrees": 45 }""", "fromDegrees 60 is not below toDegrees 45.")]
    [InlineData("""{ "material": 14, "fromDegrees": 60 }""", "fromDegrees 60 is not below toDegrees 55 (the default).")]
    [InlineData("""{ "material": 14, "toDegrees": 20 }""", "fromDegrees 40 (the default) is not below toDegrees 20.")]
    public void A_band_that_does_not_rise_is_refused(string steep, string expected)
    {
        Assert.Contains(expected, Refused(steep).Message);
    }

    // The schema refuses these before the rules see them, so the message is the schema's, naming the field.
    [Theory]
    [InlineData("""{ "material": 14, "fromDegrees": 0 }""", "fromDegrees")]
    [InlineData("""{ "material": 14, "fromDegrees": -5 }""", "fromDegrees")]
    [InlineData("""{ "material": 14, "toDegrees": 90 }""", "toDegrees")]
    [InlineData("""{ "material": 14, "toDegrees": 120 }""", "toDegrees")]
    [InlineData("""{ "fromDegrees": 40 }""", "steep")]
    [InlineData("""{ "material": 0 }""", "steep/material")]
    [InlineData("""{ "material": 14, "chain": true }""", "chain")]
    public void The_schema_refuses_a_malformed_steep_object(string steep, string field)
    {
        TileWorldException ex = Refused(steep);
        Assert.Contains("does not match the schema", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    // A catalog built in code never meets the schema, so the rules carry the range check too.
    [Theory]
    [InlineData(0f, 50f, "fromDegrees 0 is outside (0, 90).")]
    [InlineData(float.NaN, 50f, "fromDegrees NaN is outside (0, 90).")]
    [InlineData(40f, 90f, "toDegrees 90 is outside (0, 90).")]
    public void The_rules_refuse_a_band_outside_a_right_angle(float from, float to, string expected)
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog(""), "steep.json");
        GroundMaterial grass = c.Material(1)!;
        grass.Steep = new GroundMaterialSteep { Material = 14, FromDegrees = from, ToDegrees = to };

        Assert.EndsWith(expected, GroundMaterialSteepRules.Problem(grass, c));
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
        Assert.Contains("grass.json", ex.Message);
        Assert.Contains("not in the same catalog", ex.Message);
    }

    [Fact]
    public void The_validator_reports_a_companion_edited_wrong_in_memory()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog("""{ "material": 14 }"""), "steep.json");
        c.Material(1)!.Steep!.Material = 4;

        IReadOnlyList<TileWorldIssue> issues = TileWorldValidator.Validate(new TileWorldDocument(), c);

        TileWorldIssue issue = Assert.Single(issues, i => i.Code == "material.steep");
        Assert.Contains("which is Water", issue.Message);
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
        TileWorldCatalogs copy = c.CopyMaterials();

        c.Material(1)!.Steep!.FromDegrees = 35f;

        GroundMaterialSteep copied = copy.Material(1)!.Steep!;
        Assert.NotSame(c.Material(1)!.Steep, copied);
        Assert.Equal(30f, copied.FromDegrees);
        Assert.Null(copied.ToDegrees);
        Assert.Equal((ushort)14, copied.Material);
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

    [Fact]
    public void A_catalog_without_a_companion_digests_as_it_did_before_the_field()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog(""), "steep.json");
        string before = TileWorldHash.OfCatalogs(c);
        c.Material(1)!.Steep = new GroundMaterialSteep { Material = 14 };
        c.Material(1)!.Steep = null;

        Assert.Equal(before, TileWorldHash.OfCatalogs(c));
        Assert.True(c.Materials.Values.All(m => m.Steep is null));
    }
}
