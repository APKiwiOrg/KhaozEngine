using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void LodMeshRef_loads_and_round_trips_as_optional_catalog_content()
    {
        const string json = """
            { "archetypes": [ { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb",
                                "lodMeshRef": "kit/lod/tree.glb" } ] }
            """;
        TileObjectArchetype loaded = TileWorldCatalogs.LoadJson(json, "trees.json").Archetype("tree")!;

        Assert.Equal("kit/lod/tree.glb", loaded.LodMeshRef);

        string written = JsonSerializer.Serialize(
            new { archetypes = new[] { loaded } },
            CatalogWriteOptions());
        Assert.Equal("kit/lod/tree.glb",
            TileWorldCatalogs.LoadJson(written, "round-trip.json").Archetype("tree")!.LodMeshRef);
    }

    static JsonSerializerOptions CatalogWriteOptions()
    {
        var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Schema_rejects_a_non_string_lodMeshRef(string value)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(
            $$"""{ "archetypes": [ { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb", "lodMeshRef": {{value}} } ] }""",
            "bad-lod.json"));
        Assert.Contains("does not match the schema", ex.Message);
        Assert.Contains("bad-lod.json", ex.Message);
    }

    [Fact]
    public void An_omitted_or_blank_lodMeshRef_is_absent()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson("""
            { "archetypes": [ { "id": "omitted", "name": "Omitted", "meshRef": "kit/tree.glb" },
                              { "id": "blank", "name": "Blank", "meshRef": "kit/tree.glb", "lodMeshRef": "   " } ] }
            """, "optional-lod.json");

        Assert.Null(c.Archetype("omitted")!.LodMeshRef);
        Assert.Null(c.Archetype("blank")!.LodMeshRef);
    }

    [Fact]
    public void WalkSurfaces_load_and_round_trip_with_null_extents()
    {
        const string json = """
            { "archetypes": [ { "id": "bridge", "name": "Bridge", "meshRef": "kit/bridge.glb", "sizeX": 3, "sizeZ": 3,
                                "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5 },
                                                  { "height": -0.25, "minZ": -1, "maxZ": 0.5 } ] },
                              { "id": "rock", "name": "Rock", "meshRef": "kit/rock.glb" } ] }
            """;
        TileWorldCatalogs loaded = TileWorldCatalogs.LoadJson(json, "bridges.json");
        TileObjectArchetype bridge = loaded.Archetype("bridge")!;

        AssertDeck(bridge);
        Assert.Null(loaded.Archetype("rock")!.WalkSurfaces);

        string written = JsonSerializer.Serialize(new { archetypes = new[] { bridge } }, CatalogWriteOptions());
        Assert.DoesNotContain("minZ\":null", written);
        AssertDeck(TileWorldCatalogs.LoadJson(written, "round-trip.json").Archetype("bridge")!);

        static void AssertDeck(TileObjectArchetype a)
        {
            Assert.Equal(2, a.WalkSurfaces!.Count);
            TileWalkSurface deck = a.WalkSurfaces[0];
            Assert.Equal((0.825f, -2.5f, 2.5f), (deck.Height, deck.MinX!.Value, deck.MaxX!.Value));
            Assert.Null(deck.MinZ);
            Assert.Null(deck.MaxZ);
            TileWalkSurface sunk = a.WalkSurfaces[1];
            Assert.Equal((-0.25f, -1f, 0.5f), (sunk.Height, sunk.MinZ!.Value, sunk.MaxZ!.Value));
            Assert.Null(sunk.MinX);
            Assert.Null(sunk.MaxX);
        }
    }

    [Fact]
    public void An_empty_walkSurfaces_list_loads_as_none()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            """{ "archetypes": [ { "id": "a", "name": "A", "meshRef": "m", "walkSurfaces": [] } ] }""", "empty.json");

        Assert.Null(c.Archetype("a")!.WalkSurfaces);
    }

    [Theory]
    [InlineData("""{ "minX": 1 }""")]
    [InlineData("""{ "height": 1, "top": 2 }""")]
    [InlineData("""{ "height": "high" }""")]
    [InlineData("""{ "height": 1, "minX": null }""")]
    public void Schema_rejects_a_malformed_walk_surface(string surface)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(
            $$"""{ "archetypes": [ { "id": "deck", "name": "Deck", "meshRef": "m", "walkSurfaces": [ {{surface}} ] } ] }""",
            "bad-surface.json"));
        Assert.Contains("does not match the schema", ex.Message);
        Assert.Contains("bad-surface.json", ex.Message);
    }

    [Theory]
    [InlineData("\"minX\": 2, \"maxX\": -2", "minX")]
    [InlineData("\"minX\": 1, \"maxX\": 1", "minX")]
    [InlineData("\"minZ\": 0.5, \"maxZ\": 0.25", "minZ")]
    public void A_walk_surface_whose_min_is_not_below_its_max_refuses_to_load(string extents, string field)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(
            $$"""{ "archetypes": [ { "id": "deck", "name": "Deck", "meshRef": "m", "walkSurfaces": [ { "height": 0 }, { "height": 1, {{extents}} } ] } ] }""",
            "inverted.json"));
        Assert.Contains("inverted.json", ex.Message);
        Assert.Contains("'deck'", ex.Message);
        Assert.Contains("walk surface 1", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void A_one_sided_extent_is_not_judged_at_load()
    {
        // The other side resolves against a tile size the catalog does not know, so this rect is only inverted in a
        // world whose tiles are narrower than ten metres, and that is the query's business rather than the loader's.
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            """{ "archetypes": [ { "id": "deck", "name": "Deck", "meshRef": "m", "walkSurfaces": [ { "height": 1, "minX": 5 } ] } ] }""",
            "one-sided.json");

        Assert.Equal(5f, c.Archetype("deck")!.WalkSurfaces![0].MinX);
    }

    [Theory]
    [InlineData("height")]
    [InlineData("minX")]
    [InlineData("maxZ")]
    public void A_non_finite_walk_surface_value_refuses_to_merge(string field)
    {
        // JSON cannot carry a NaN, but a loaded catalog is mutable and Merge re-adds every archetype through the same
        // gate, so an in-memory edit cannot smuggle one past it.
        TileWorldCatalogs part = TileWorldCatalogs.LoadJson(
            """{ "archetypes": [ { "id": "deck", "name": "Deck", "meshRef": "m", "walkSurfaces": [ { "height": 1 } ] } ] }""",
            "deck.json");
        TileWalkSurface surface = part.Archetype("deck")!.WalkSurfaces![0];
        switch (field)
        {
            case "height": surface.Height = float.NaN; break;
            case "minX": surface.MinX = float.PositiveInfinity; break;
            default: surface.MaxZ = float.NegativeInfinity; break;
        }

        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.Merge(part));
        Assert.Contains("'deck'", ex.Message);
        Assert.Contains(field, ex.Message);
        Assert.Contains("not a finite number", ex.Message);
    }

    [Fact]
    public void A_walk_surface_height_past_the_float_range_refuses_to_load()
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(
            """{ "archetypes": [ { "id": "deck", "name": "Deck", "meshRef": "m", "walkSurfaces": [ { "height": 1e39 } ] } ] }""",
            "overflow.json"));
        Assert.Contains("overflow.json", ex.Message);
    }

    [Fact]
    public void Catalog_hash_applies_the_existing_cosmetic_mesh_policy_to_the_lod_mesh()
    {
        TileWorldCatalogs baseline = TileWorldCatalogs.LoadJson("""
            { "archetypes": [ { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb" } ] }
            """, "baseline.json");
        TileWorldCatalogs changedFull = TileWorldCatalogs.LoadJson("""
            { "archetypes": [ { "id": "tree", "name": "Tree", "meshRef": "kit/tree-v2.glb" } ] }
            """, "full.json");
        TileWorldCatalogs changedLod = TileWorldCatalogs.LoadJson("""
            { "archetypes": [ { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb",
                                "lodMeshRef": "kit/lod/tree.glb" } ] }
            """, "lod.json");

        string baselineHash = TileWorldHash.OfCatalogs(baseline);
        Assert.NotEqual(baselineHash, TileWorldHash.OfCatalogs(changedFull));
        Assert.NotEqual(baselineHash, TileWorldHash.OfCatalogs(changedLod));
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
