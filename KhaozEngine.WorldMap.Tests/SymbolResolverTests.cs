using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves ordered, all-of footprint symbol selection.</summary>
public sealed class SymbolResolverTests
{
    static readonly WorldMapFootprint Footprint = new(1, "hut", new Vector2(4f), new Vector2(1f),
        0f, ["npc", "shopkeeper"], true, WorldMapCollision.Solid);
    static readonly WorldMapSymbol Building = new(WorldMapSymbolKind.Building, Color.White, Color.Black);
    static readonly WorldMapSymbol Tree = new(WorldMapSymbolKind.Tree, Color.FromHex("#00ff00"), Color.Black);

    /// <summary>A later matching rule cannot replace the first matching rule.</summary>
    [Fact]
    public void FirstMatchingRuleWins()
    {
        var style = new WorldMapStyle
        {
            SymbolRules = [new(new(ArchetypeKey: "other"), Tree), new(new(AllTags: ["npc"]), Building), new(new(), Tree)]
        };
        Assert.Same(Building, WorldMapSymbolResolver.SymbolFor(style, Footprint));
    }

    /// <summary>Tags match every required value ordinally, with no restriction for an empty list.</summary>
    [Fact]
    public void AllTags_RequiresEveryTag()
    {
        var match = new WorldMapFootprintMatch(AllTags: ["npc", "shopkeeper"]);
        Assert.False(WorldMapSymbolResolver.Matches(match, Footprint with { Tags = ["npc"] }));
        Assert.True(WorldMapSymbolResolver.Matches(match, Footprint));
        Assert.False(WorldMapSymbolResolver.Matches(match, Footprint with { Tags = ["NPC", "shopkeeper"] }));
        Assert.True(WorldMapSymbolResolver.Matches(new(AllTags: []), Footprint with { Tags = [] }));
    }

    /// <summary>Only unmatched blocking footprints use the style's fallback symbol.</summary>
    [Fact]
    public void NoMatch_BlockingFallsBackToObstacle_NonBlockingIsHidden()
    {
        var style = new WorldMapStyle { SymbolRules = [new(new(ArchetypeKey: "other"), Tree)] };
        Assert.Equal(WorldMapSymbolKind.Obstacle, WorldMapSymbolResolver.SymbolFor(style, Footprint).Kind);
        Assert.Same(style.FallbackBlocking, WorldMapSymbolResolver.SymbolFor(style, Footprint));
        Assert.Same(WorldMapSymbol.Hidden,
            WorldMapSymbolResolver.SymbolFor(style, Footprint with { Collision = WorldMapCollision.None }));
        Assert.Same(style.FallbackBlocking,
            WorldMapSymbolResolver.SymbolFor(style, Footprint with { Collision = WorldMapCollision.Wall }));
    }

    /// <summary>All non-null fields must agree, including false roof flags and non-blocking collision.</summary>
    [Fact]
    public void IsRoofAndCollisionFieldsFilter()
    {
        var match = new WorldMapFootprintMatch("hut", ["npc"], true, WorldMapCollision.Solid);
        Assert.True(WorldMapSymbolResolver.Matches(match, Footprint));
        Assert.False(WorldMapSymbolResolver.Matches(match, Footprint with { IsRoof = false }));
        Assert.False(WorldMapSymbolResolver.Matches(match, Footprint with { Collision = WorldMapCollision.Wall }));
        Assert.False(WorldMapSymbolResolver.Matches(match, Footprint with { ArchetypeKey = "Hut" }));
        Assert.True(WorldMapSymbolResolver.Matches(new(), Footprint));
        Assert.True(WorldMapSymbolResolver.Matches(new(IsRoof: false, Collision: WorldMapCollision.None),
            Footprint with { IsRoof = false, Collision = WorldMapCollision.None }));
    }
}
