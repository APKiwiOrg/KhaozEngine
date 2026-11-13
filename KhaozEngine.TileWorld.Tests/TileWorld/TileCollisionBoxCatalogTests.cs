using System;
using System.Text.Json;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The <c>collisionBoxes</c> catalog field: what loads and round-trips through a serializer, and what the
/// loader refuses on top of the schema (the collision kinds that may carry boxes, non-finite values and boxes that are
/// inverted or empty). Issue https://github.com/APKiwiOrg/KhaozEngine/issues/1782.</summary>
public class TileCollisionBoxCatalogTests
{
    // A 2x1 bench: a seat resting on the base, and a back raised off it and turned a little, so both defaults and
    // every authored field round-trip.
    const string Bench = """
        { "archetypes": [ { "id": "bench", "name": "Bench", "meshRef": "kit/bench.glb", "sizeX": 2, "sizeZ": 1,
                            "collisionKind": "Solid",
                            "collisionBoxes": [
                              { "minX": -0.8, "maxX": 0.8, "minZ": -0.2, "maxZ": 0.3, "maxY": 0.45 },
                              { "minX": -0.8, "maxX": 0.8, "minY": 0.45, "maxY": 0.94, "minZ": -0.3, "maxZ": -0.2, "yawDegrees": 5 } ] } ] }
        """;

    static string Boxed(string kind, string box = """{ "minX": -0.3, "maxX": 0.3, "minZ": -0.3, "maxZ": 0.3, "maxY": 0.9 }""") =>
        $$"""{ "archetypes": [ { "id": "prop", "name": "Prop", "meshRef": "m", "collisionKind": "{{kind}}", "collisionBoxes": [ {{box}} ] } ] }""";

    [Fact]
    public void Boxes_load_with_their_defaults_and_round_trip()
    {
        TileObjectArchetype bench = TileWorldCatalogs.LoadJson(Bench, "bench.json").Archetype("bench")!;
        AssertBench(bench);

        string written = JsonSerializer.Serialize(new { archetypes = new[] { bench } },
            TileWorldCatalogsTests.CatalogWriteOptions());
        AssertBench(TileWorldCatalogs.LoadJson(written, "round-trip.json").Archetype("bench")!);

        static void AssertBench(TileObjectArchetype a)
        {
            Assert.Equal(2, a.CollisionBoxes!.Count);
            TileCollisionBox seat = a.CollisionBoxes[0];
            Assert.Equal((-0.8f, 0.8f, 0f, 0.45f, -0.2f, 0.3f, 0f),
                (seat.MinX, seat.MaxX, seat.MinY, seat.MaxY, seat.MinZ, seat.MaxZ, seat.YawDegrees));
            TileCollisionBox back = a.CollisionBoxes[1];
            Assert.Equal((-0.8f, 0.8f, 0.45f, 0.94f, -0.3f, -0.2f, 5f),
                (back.MinX, back.MaxX, back.MinY, back.MaxY, back.MinZ, back.MaxZ, back.YawDegrees));
        }
    }

    // An empty list is no boxes at all, so it carries none of the rules a list does, the collision kind included.
    [Fact]
    public void An_empty_box_list_loads_as_none_on_any_kind()
    {
        TileWorldCatalogs c = TileWorldCatalogs.LoadJson(
            """{ "archetypes": [ { "id": "rug", "name": "Rug", "meshRef": "m", "collisionKind": "None", "collisionBoxes": [] } ] }""",
            "empty.json");

        Assert.Null(c.Archetype("rug")!.CollisionBoxes);
    }

    [Theory]
    [InlineData("""{ "minX": -1, "maxX": 1, "minZ": -1, "maxZ": 1 }""")]
    [InlineData("""{ "minX": -1, "maxX": 1, "minZ": -1, "maxZ": 1, "maxY": 1, "top": 2 }""")]
    [InlineData("""{ "minX": -1, "maxX": 1, "minZ": -1, "maxZ": 1, "maxY": "tall" }""")]
    [InlineData("""{ "minX": -1, "maxX": 1, "minZ": -1, "maxZ": 1, "maxY": 1, "yawDegrees": null }""")]
    [InlineData("null")]
    public void Schema_rejects_a_malformed_box(string box)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(Boxed("Solid", box), "bad-box.json"));

        Assert.Equal(TileWorldRefusal.CatalogSchemaMismatch, ex.Reason);
        Assert.Equal("bad-box.json", ex.SourceName);
    }

    // The two kinds that build an object collider take boxes. The three that do not refuse them, rather than load boxes
    // physics would silently ignore.
    [Theory]
    [InlineData(TileCollisionKind.Solid, true)]
    [InlineData(TileCollisionKind.Diagonal, true)]
    [InlineData(TileCollisionKind.None, false)]
    [InlineData(TileCollisionKind.Wall, false)]
    [InlineData(TileCollisionKind.WallCorner, false)]
    public void Only_a_kind_that_builds_an_object_collider_takes_boxes(TileCollisionKind kind, bool admitted)
    {
        string json = Boxed(kind.ToString());

        if (admitted)
        {
            Assert.Single(TileWorldCatalogs.LoadJson(json, "kind.json").Archetype("prop")!.CollisionBoxes!);
            return;
        }
        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(json, "kind.json"));
        Assert.Equal(TileWorldRefusal.CatalogCollisionBoxKindUnsupported, ex.Reason);
        Assert.Equal("kind.json", ex.SourceName);
        Assert.Equal("prop", ex.ArchetypeId);
        Assert.Equal(kind, ex.CollisionKind);
    }

    [Theory]
    [InlineData("""{ "minX": 0.3, "maxX": -0.3, "minZ": -0.3, "maxZ": 0.3, "maxY": 0.9 }""", "minX", "maxX", 0.3f, -0.3f)]
    [InlineData("""{ "minX": -0.3, "maxX": 0.3, "minY": 0.9, "maxY": 0.9, "minZ": -0.3, "maxZ": 0.3 }""", "minY", "maxY", 0.9f, 0.9f)]
    [InlineData("""{ "minX": -0.3, "maxX": 0.3, "minZ": 0.5, "maxZ": 0.25, "maxY": 0.9 }""", "minZ", "maxZ", 0.5f, 0.25f)]
    public void A_box_whose_min_is_not_below_its_max_refuses_to_load(string box, string minField, string maxField,
        float minimum, float maximum)
    {
        string json = $$"""
            { "archetypes": [ { "id": "prop", "name": "Prop", "meshRef": "m", "collisionKind": "Solid",
                                "collisionBoxes": [ { "minX": -0.3, "maxX": 0.3, "minZ": -0.3, "maxZ": 0.3, "maxY": 0.9 }, {{box}} ] } ] }
            """;

        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.LoadJson(json, "inverted.json"));

        Assert.Equal(TileWorldRefusal.CatalogCollisionBoxBoundsUnordered, ex.Reason);
        Assert.Equal("inverted.json", ex.SourceName);
        Assert.Equal("prop", ex.ArchetypeId);
        Assert.Equal(1, ex.CollisionBoxIndex);
        Assert.Equal(minField, ex.MinField);
        Assert.Equal(maxField, ex.MaxField);
        Assert.Equal(minimum, ex.Minimum);
        Assert.Equal(maximum, ex.Maximum);
    }

    // JSON cannot carry a NaN, but a loaded catalog is mutable and Merge re-adds every archetype through the same gate,
    // so an in-memory edit cannot smuggle one into a box.
    [Theory]
    [InlineData("minX", float.NaN)]
    [InlineData("minY", float.NegativeInfinity)]
    [InlineData("yawDegrees", float.PositiveInfinity)]
    public void A_non_finite_box_value_refuses_to_merge(string field, float value)
    {
        TileWorldCatalogs part = TileWorldCatalogs.LoadJson(Bench, "bench.json");
        Assert.NotNull(TileWorldCatalogs.Merge(part).Archetype("bench"));
        TileCollisionBox back = part.Archetype("bench")!.CollisionBoxes![1];
        switch (field)
        {
            case "minX": back.MinX = value; break;
            case "minY": back.MinY = value; break;
            default: back.YawDegrees = value; break;
        }

        var ex = Assert.Throws<TileWorldException>(() => TileWorldCatalogs.Merge(part));

        Assert.Equal(TileWorldRefusal.CatalogCollisionBoxNonFinite, ex.Reason);
        Assert.Equal("bench.json", ex.SourceName);
        Assert.Equal("bench", ex.ArchetypeId);
        Assert.Equal(1, ex.CollisionBoxIndex);
        Assert.Equal(field, ex.Field);
        Assert.Equal(value, ex.Value);
    }
}
