using System.IO;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldCatalogsTests
{
    const string Ground = """
        { "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a", "kind": "Ground" },
                         { "id": 4, "name": "water", "color": "#2a5a9a", "kind": "Water" } ] }
        """;
    const string Arch = """
        { "archetypes": [ { "id": "wall", "name": "Wall", "meshRef": "kit/wall.glb", "collisionKind": "Wall" },
                          { "id": "rock", "name": "Rock", "meshRef": "kit/rock.glb", "sizeX": 2, "sizeZ": 3, "collisionKind": "Solid", "tags": ["nature"] } ] }
        """;

    [Fact]
    public void LoadJson_reads_both_kinds_with_defaults()
    {
        TileWorldCatalogs c = TileWorldCatalogs.Merge(TileWorldCatalogs.LoadJson(Ground, "g"), TileWorldCatalogs.LoadJson(Arch, "a"));
        Assert.Equal(GroundMaterialKind.Water, c.Material(4)!.Kind);
        Assert.Null(c.Material(9));
        TileObjectArchetype wall = c.Archetype("wall")!;
        Assert.Equal(1, wall.SizeX);
        Assert.Equal(TileCollisionKind.Wall, wall.CollisionKind);
        Assert.False(wall.IsRoof);
        Assert.Equal(new[] { "nature" }, c.Archetype("rock")!.Tags);
    }

    [Fact]
    public void Load_reads_files_and_names_duplicates()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Sub("g.json"), Ground);
        File.WriteAllText(tmp.Sub("g2.json"), Ground);
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.Load(new[] { tmp.Sub("g.json"), tmp.Sub("g2.json") }));
        Assert.Contains("material 1", ex.Message);
        Assert.Contains("g2.json", ex.Message);
        Assert.Contains("g.json", ex.Message);
    }

    [Fact]
    public void Malformed_json_names_the_source()
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson("{ oops", "bad.json"));
        Assert.Contains("bad.json", ex.Message);
    }

    // Pins the embedded schema itself. Every case here is one the JsonStringEnumConverter and the
    // deserializer would happily accept, so gutting the schema to {} fails this test and only this test.
    [Theory]
    [InlineData("""{ "materials": [ { "id": 0, "name": "x", "color": "#000000" } ] }""")]
    [InlineData("""{ "archetypes": [ { "id": "a", "name": "A", "meshRef": "m", "sizeX": 0 } ] }""")]
    [InlineData("""{ "bogus": 1 }""")]
    public void Schema_rejects_what_the_converter_would_accept(string json)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(json, "pin.json"));
        Assert.Contains("does not match the schema", ex.Message);
        Assert.Contains("pin.json", ex.Message);
    }

    [Fact]
    public void Schema_rejects_a_bad_kind_and_names_the_source()
    {
        var ex = Assert.Throws<TileWorldException>(() =>
            TileWorldCatalogs.LoadJson("""{ "materials": [ { "id": 1, "name": "x", "color": "#000000", "kind": "Lava" } ] }""", "bad.json"));
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void Greybox_is_non_empty_and_self_consistent()
    {
        TileWorldCatalogs g = TileWorldCatalogs.Greybox();
        Assert.True(g.Materials.Count >= 6);
        Assert.Equal(TileCollisionKind.Solid, g.Archetype("rock_large")!.CollisionKind);
        Assert.Equal((2, 2), TileFootprint.Rotated(g.Archetype("rock_large")!, 1));
        Assert.Equal(TileCollisionKind.Solid, g.Archetype("bench")!.CollisionKind);
        Assert.Equal((1, 2), TileFootprint.Rotated(g.Archetype("bench")!, 0));
        Assert.Equal((2, 1), TileFootprint.Rotated(g.Archetype("bench")!, 1));
        Assert.True(g.Archetype("roof_flat")!.IsRoof);
    }

    [Fact]
    public void A_null_or_blank_archetype_id_is_simply_undefined()
    {
        // Content can carry "archetypeId": null, and the validator has to be able to ASK about it without
        // a Dictionary.TryGetValue(null) throw taking the whole validation pass down.
        TileWorldCatalogs g = TileWorldCatalogs.Greybox();
        Assert.Null(g.Archetype(null));
        Assert.Null(g.Archetype(""));
        Assert.Null(g.Archetype("   "));
    }

    [Fact]
    public void Footprint_rotation_swaps_axes_and_anchors_at_the_SW_tile()
    {
        var a = new TileObjectArchetype { Id = "a", SizeX = 2, SizeZ = 3 };
        Assert.Equal((2, 3), TileFootprint.Rotated(a, 0));
        Assert.Equal((3, 2), TileFootprint.Rotated(a, 1));
        Assert.Equal((2, 3), TileFootprint.Rotated(a, 2));
        Assert.Equal(new TileRect(10, 20, 3, 2), TileFootprint.Of(a, 10, 20, 3));
    }

    const string Textured = """
        { "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a", "texture": "grass.png", "tilesPerMetre": 0.25 },
                         { "id": 2, "name": "dirt", "color": "#8a6a3a" } ] }
        """;
    const string OneMaterial = """{ "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a" } ] }""";
    const string OtherMaterial = """{ "materials": [ { "id": 2, "name": "dirt", "color": "#8a6a3a" } ] }""";

    [Fact]
    public void TilesPerMetre_round_trips_and_is_null_when_the_material_omits_it()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(Textured, "t.json");
        Assert.Equal(0.25f, c.Material(1)!.TilesPerMetre!.Value);
        Assert.Null(c.Material(2)!.TilesPerMetre);
    }

    // The schema is what rejects these: the deserializer takes any float, so a zero repeat would reach the
    // renderer as a divide-by-nothing UV scale.
    [Theory]
    [InlineData("-0.5")]
    [InlineData("0")]
    public void Schema_rejects_a_non_positive_tilesPerMetre(string value)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(
            $$"""{ "materials": [ { "id": 1, "name": "x", "color": "#000000", "tilesPerMetre": {{value}} } ] }""", "bad.json"));
        Assert.Contains("does not match the schema", ex.Message);
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void MaterialSource_is_the_file_each_material_was_loaded_from()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Sub("a.json"), OneMaterial);
        File.WriteAllText(tmp.Sub("b.json"), OtherMaterial);
        TileWorldCatalogs c = TileWorldCatalogs.Load(new[] { tmp.Sub("a.json"), tmp.Sub("b.json") });
        Assert.Equal(tmp.Sub("a.json"), c.MaterialSource(1));
        Assert.Equal(tmp.Sub("b.json"), c.MaterialSource(2));
        Assert.Null(c.MaterialSource(9));
    }

    [Fact]
    public void MaterialSource_is_null_when_the_catalog_did_not_come_from_a_file()
    {
        Assert.Null(TileWorldCatalogs.LoadJson(Ground, "g").MaterialSource(1));
        Assert.Null(TileWorldCatalogs.Merge(TileWorldCatalogs.LoadJson(Ground, "g")).MaterialSource(1));
        Assert.Null(TileWorldCatalogs.Greybox().MaterialSource(1));
    }

    [Fact]
    public void Merge_keeps_the_material_source_of_each_part()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Sub("a.json"), OneMaterial);
        TileWorldCatalogs merged = TileWorldCatalogs.Merge(
            TileWorldCatalogs.Load(new[] { tmp.Sub("a.json") }),
            TileWorldCatalogs.LoadJson(OtherMaterial, "label"));
        Assert.Equal(tmp.Sub("a.json"), merged.MaterialSource(1));
        Assert.Null(merged.MaterialSource(2));
    }
}
