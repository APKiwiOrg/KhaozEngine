using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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

    static void AssertDiagnosticValues(string message, string[] values)
    {
        foreach (string value in values)
            Assert.Matches(@"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", message);
    }

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
    [InlineData("{ \"material\": 99 }", new[] { "steep.json", "1", "grass", "99" })]
    [InlineData("{ \"material\": 1 }", new[] { "steep.json", "1", "grass" })]
    [InlineData("{ \"material\": 4 }", new[] { "steep.json", "1", "grass", "4", "water", "Water" })]
    public void An_invalid_companion_is_refused_by_name(string steep, string[] diagnosticValues)
    {
        TileWorldException ex = Refused(steep);

        AssertDiagnosticValues(ex.Message, diagnosticValues);
    }

    [Theory]
    [InlineData("""{ "material": 14, "fromDegrees": 50, "toDegrees": 50 }""", new[] { "fromDegrees", "50", "toDegrees" })]
    [InlineData("""{ "material": 14, "fromDegrees": 60, "toDegrees": 45 }""", new[] { "fromDegrees", "60", "toDegrees", "45" })]
    [InlineData("""{ "material": 14, "fromDegrees": 60 }""", new[] { "fromDegrees", "60", "toDegrees", "55" })]
    [InlineData("""{ "material": 14, "toDegrees": 20 }""", new[] { "fromDegrees", "40", "toDegrees", "20" })]
    public void A_band_that_does_not_rise_is_refused(string steep, string[] diagnosticValues)
    {
        TileWorldException error = Refused(steep);
        AssertDiagnosticValues(error.Message, diagnosticValues);
    }

    // Both ends pack to cos 1 in single precision, so the renderer's smoothstep would have equal edges, which GLSL
    // leaves undefined. The catalog refuses it at load rather than leaving it to the GPU material load.
    [Fact]
    public void A_band_whose_ends_share_a_cosine_is_refused_at_load()
    {
        TileWorldException ex = Refused("""{ "material": 14, "fromDegrees": 0.001, "toDegrees": 0.002 }""");
        Assert.Contains("steep.json", ex.Message);
        Assert.Contains("0.001", ex.Message);
        Assert.Contains("0.002", ex.Message);
    }

    [Fact]
    public void A_narrow_band_that_still_resolves_loads()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            Catalog("""{ "material": 14, "fromDegrees": 40, "toDegrees": 40.5 }"""), "steep.json");
        Assert.Equal(40.5f, c.Material(1)!.Steep!.ResolvedToDegrees);
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
        Assert.Contains(field, ex.Message);
    }

    // A catalog built in code never meets the schema, so the rules carry the range check too.
    [Theory]
    [InlineData(0f, 50f, new[] { "fromDegrees", "0", "90" })]
    [InlineData(float.NaN, 50f, new[] { "fromDegrees", "NaN", "0", "90" })]
    [InlineData(40f, 90f, new[] { "toDegrees", "90", "0" })]
    public void The_rules_refuse_a_band_outside_a_right_angle(float from, float to, string[] diagnosticValues)
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Catalog(""), "steep.json");
        GroundMaterial grass = c.Material(1)!;
        grass.Steep = new GroundMaterialSteep { Material = 14, FromDegrees = from, ToDegrees = to };

        string problem = Assert.IsType<string>(GroundMaterialSteepRules.Problem(grass, c));
        AssertDiagnosticValues(problem, diagnosticValues);
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
        Assert.Contains("grass", ex.Message);
        Assert.Contains("14", ex.Message);
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
}
