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
        Assert.True(g.Archetype("roof_flat")!.IsRoof);
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
}
