using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves chunk ownership, ordered tag rules and deterministic feature anchors.</summary>
public sealed class FeatureCollectorTests
{
    static readonly WorldMapLabelTier Town = new("map-label:town", 2, 1f, 8f, 1.25f);
    static readonly WorldMapLabelTier Region = new("map-label:region", 1, 0.25f, 4f, 1.5f);

    /// <summary>A crossing footprint contributes its icon only to the chunk containing its centre.</summary>
    [Fact]
    public void FootprintIcon_AnchoredAtCentre_OnlyInOwningChunk()
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(63.5f, 10f), ["bank"]) with { HalfExtents = new(2f, 1f) });
        var style = new WorldMapStyle
        {
            IconRules = [new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 7, 18f)]
        };

        WorldMapChunkFeatures owner = WorldMapFeatureCollector.Collect(source, style, new(0, 0));
        Assert.Equal(new WorldMapIconAnchor("bank", new(63.5f, 10f), 7, 18f), Assert.Single(owner.Icons));
        Assert.Empty(WorldMapFeatureCollector.Collect(source, style, new(1, 0)).Icons);
    }

    /// <summary>A marker must carry every tag required by its icon rule.</summary>
    [Fact]
    public void MarkerIcon_RequiresAllTags()
    {
        var source = Source();
        source.Markers.Add(new("shop", new(10f, 10f), ["npc", "shopkeeper"]));
        source.Markers.Add(new("npc", new(20f, 10f), ["npc"]));
        var style = new WorldMapStyle
        {
            IconRules = [new(WorldMapFeatureSource.Marker, ["npc", "shopkeeper"], "shop", 3)]
        };

        WorldMapIconAnchor icon = Assert.Single(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons);
        Assert.Equal(new WorldMapIconAnchor("shop", new(10f, 10f), 3, 24f), icon);
    }

    /// <summary>The first matching rule wins regardless of a later rule's priority.</summary>
    [Fact]
    public void OneIconPerFeature_FirstRuleWins()
    {
        var source = Source();
        source.Markers.Add(new("shop", new(10f, 10f), ["npc", "shopkeeper"]));
        source.Footprints.Add(Footprint(new(20f, 10f), ["npc", "shopkeeper"]));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Marker, ["missing"], "missing", 100),
                new(WorldMapFeatureSource.Marker, ["npc"], "marker-first", 1, 12f),
                new(WorldMapFeatureSource.Marker, ["shopkeeper"], "marker-later", 99),
                new(WorldMapFeatureSource.Footprint, ["npc"], "footprint-first", 2, 16f),
                new(WorldMapFeatureSource.Footprint, ["shopkeeper"], "footprint-later", 100)
            ]
        };

        Assert.Equal(
            new WorldMapIconAnchor[]
            {
                new("marker-first", new(10f, 10f), 1, 12f),
                new("footprint-first", new(20f, 10f), 2, 16f)
            },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons);
    }

    /// <summary>A label preserves the marker name, position and matching tier.</summary>
    [Fact]
    public void Label_TakesTierFromTag()
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 12f), ["map-label", "map-label:town"]));
        var style = new WorldMapStyle { Labels = new("map-label", [Region, Town]) };

        WorldMapLabelAnchor label = Assert.Single(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels);
        Assert.Equal(new WorldMapLabelAnchor("Hollowmere", new(10f, 12f), Town), label);
        Assert.Same(Town, label.Tier);
    }

    /// <summary>The label tag alone does not select any tier.</summary>
    [Fact]
    public void Label_WithoutTierTag_IsSkipped()
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 12f), ["map-label"]));
        var style = new WorldMapStyle { Labels = new("map-label", [Town]) };

        Assert.Empty(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels);
    }

    /// <summary>Disabled label rules do not create labels from tagged markers.</summary>
    [Fact]
    public void NoLabelRules_NoLabels()
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 12f), ["map-label", "map-label:town"]));

        Assert.Empty(WorldMapFeatureCollector.Collect(source, new(), new(0, 0)).Labels);
    }

    /// <summary>An icon rule cannot match the other kind of authored feature.</summary>
    [Theory]
    [InlineData(WorldMapFeatureSource.Footprint, 10f)]
    [InlineData(WorldMapFeatureSource.Marker, 20f)]
    public void IconRule_MatchesOnlyItsSource(WorldMapFeatureSource kind, float expectedX)
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(10f, 10f), ["bank"]));
        source.Markers.Add(new("bank", new(20f, 10f), ["bank"]));
        var style = new WorldMapStyle { IconRules = [new(kind, ["bank"], "bank", 1)] };

        WorldMapIconAnchor icon = Assert.Single(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons);
        Assert.Equal(new Vector2(expectedX, 10f), icon.Position);
    }

    /// <summary>Footprint icon matching is all-of and case-sensitive.</summary>
    [Theory]
    [InlineData("npc", "shopkeeper", true)]
    [InlineData("npc", "other", false)]
    [InlineData("NPC", "shopkeeper", false)]
    public void FootprintIcon_RequiresAllTagsOrdinally(string first, string second, bool matches)
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(10f, 10f), [first, second]));
        var style = new WorldMapStyle
        {
            IconRules = [new(WorldMapFeatureSource.Footprint, ["npc", "shopkeeper"], "shop", 1)]
        };

        Assert.Equal(matches ? 1 : 0, WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons.Count);
    }

    /// <summary>Empty tag requirements match untagged markers and footprints.</summary>
    [Fact]
    public void IconRule_EmptyTags_MatchesUntaggedFeatures()
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(10f, 10f), []));
        source.Markers.Add(new("marker", new(20f, 10f), []));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Footprint, [], "footprint", 1),
                new(WorldMapFeatureSource.Marker, [], "marker", 2)
            ]
        };

        Assert.Equal(new[] { "footprint", "marker" },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons.Select(icon => icon.IconKey));
    }

    /// <summary>A tier needs the label gate and both tag comparisons use ordinal case.</summary>
    [Theory]
    [InlineData("other", "map-label:town")]
    [InlineData("Map-label", "map-label:town")]
    [InlineData("map-label", "map-label:Town")]
    public void Label_RequiresLabelAndTierTagsOrdinally(string labelTag, string tierTag)
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 10f), [labelTag, tierTag]));
        var style = new WorldMapStyle { Labels = new("map-label", [Town]) };

        Assert.Empty(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels);
    }

    /// <summary>Tier declaration order wins even when another matching tier has a higher rank.</summary>
    [Fact]
    public void Label_FirstMatchingTierWins()
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 10f), ["map-label", "map-label:town", "map-label:region"]));
        var style = new WorldMapStyle { Labels = new("map-label", [Region, Town]) };

        Assert.Same(Region, Assert.Single(WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels).Tier);
    }

    /// <summary>An icon and label can share a marker without suppressing either feature.</summary>
    [Fact]
    public void Marker_CanProduceIconAndLabel()
    {
        var source = Source();
        source.Markers.Add(new("Hollowmere", new(10f, 10f), ["bank", "map-label", "map-label:town"]));
        var style = new WorldMapStyle
        {
            IconRules = [new(WorldMapFeatureSource.Marker, ["bank"], "bank", 1)],
            Labels = new("map-label", [Town])
        };

        WorldMapChunkFeatures features = WorldMapFeatureCollector.Collect(source, style, new(0, 0));
        Assert.Equal("bank", Assert.Single(features.Icons).IconKey);
        Assert.Equal("Hollowmere", Assert.Single(features.Labels).MarkerName);
    }

    /// <summary>Exact positive and negative chunk borders belong only to the north-east chunk.</summary>
    [Theory]
    [InlineData(64f, 10f, 1, 0)]
    [InlineData(10f, 64f, 0, 1)]
    [InlineData(-64f, -64f, -1, -1)]
    public void BoundaryAnchors_OnlyInOwningChunk(float x, float y, int chunkX, int chunkY)
    {
        var source = new TestWorldMapSource(new(-128f, -128f, 256f, 256f));
        var position = new Vector2(x, y);
        source.Footprints.Add(Footprint(position, ["bank"]));
        source.Markers.Add(new("Hollowmere", position, ["bank", "map-label", "map-label:town"]));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 1),
                new(WorldMapFeatureSource.Marker, ["bank"], "bank", 1)
            ],
            Labels = new("map-label", [Town])
        };

        WorldMapChunkFeatures owner = WorldMapFeatureCollector.Collect(source, style, new(chunkX, chunkY));
        Assert.Equal(2, owner.Icons.Count);
        Assert.All(owner.Icons, icon => Assert.Equal(position, icon.Position));
        Assert.Equal(position, Assert.Single(owner.Labels).Position);
        foreach (WorldMapChunkCoord adjacent in new WorldMapChunkCoord[]
        {
            new(chunkX - 1, chunkY), new(chunkX, chunkY - 1), new(chunkX - 1, chunkY - 1)
        })
        {
            WorldMapChunkFeatures features = WorldMapFeatureCollector.Collect(source, style, adjacent);
            Assert.Empty(features.Icons);
            Assert.Empty(features.Labels);
        }
    }

    /// <summary>Icon ordering spans both source types and breaks position ties by ordinal key.</summary>
    [Fact]
    public void Icons_SortedNorthToSouthThenWestToEastThenOrdinalKey()
    {
        var source = Source();
        source.Markers.Add(new("a", new(10f, 20f), ["a"]));
        source.Markers.Add(new("south", new(1f, 2f), ["south"]));
        source.Markers.Add(new("west", new(5f, 20f), ["west"]));
        source.Markers.Add(new("A", new(10f, 20f), ["A"]));
        source.Footprints.Add(Footprint(new(30f, 40f), []));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Marker, ["a"], "a", 100),
                new(WorldMapFeatureSource.Marker, ["south"], "south", 100),
                new(WorldMapFeatureSource.Marker, ["west"], "west", 100),
                new(WorldMapFeatureSource.Marker, ["A"], "A", 1),
                new(WorldMapFeatureSource.Footprint, [], "north", 1)
            ]
        };

        Assert.Equal(new[] { "north", "west", "A", "a", "south" },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons.Select(icon => icon.IconKey));
        source.Markers.Reverse();
        Assert.Equal(new[] { "north", "west", "A", "a", "south" },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Icons.Select(icon => icon.IconKey));
    }

    /// <summary>Label ordering uses marker names as the ordinal final tie-breaker.</summary>
    [Fact]
    public void Labels_SortedNorthToSouthThenWestToEastThenOrdinalName()
    {
        var source = Source();
        string[] tags = ["map-label", "map-label:town"];
        source.Markers.Add(new("a", new(10f, 20f), tags));
        source.Markers.Add(new("south", new(1f, 2f), tags));
        source.Markers.Add(new("west", new(5f, 20f), tags));
        source.Markers.Add(new("A", new(10f, 20f), tags));
        source.Markers.Add(new("north", new(30f, 40f), tags));
        var style = new WorldMapStyle { Labels = new("map-label", [Town]) };

        Assert.Equal(new[] { "north", "west", "A", "a", "south" },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels.Select(label => label.MarkerName));
        source.Markers.Reverse();
        Assert.Equal(new[] { "north", "west", "A", "a", "south" },
            WorldMapFeatureCollector.Collect(source, style, new(0, 0)).Labels.Select(label => label.MarkerName));
    }

    /// <summary>A source with no matches produces empty icon and label collections.</summary>
    [Fact]
    public void NoMatchingFeatures_ReturnsEmptyCollections()
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(10f, 10f), ["other"]));
        source.Markers.Add(new("other", new(20f, 10f), ["other"]));

        WorldMapChunkFeatures features = WorldMapFeatureCollector.Collect(source, new(), new(0, 0));
        Assert.Empty(features.Icons);
        Assert.Empty(features.Labels);
        Assert.Empty(WorldMapChunkFeatures.Empty.Icons);
        Assert.Empty(WorldMapChunkFeatures.Empty.Labels);
    }

    /// <summary>Null public inputs fail at the collector boundary.</summary>
    [Fact]
    public void Collect_NullInputs_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => WorldMapFeatureCollector.Collect(null!, new(), new(0, 0)));
        Assert.Throws<ArgumentNullException>(() => WorldMapFeatureCollector.Collect(Source(), null!, new(0, 0)));
    }

    static TestWorldMapSource Source() => new(new(0f, 0f, 128f, 128f));

    static WorldMapFootprint Footprint(Vector2 center, IReadOnlyList<string> tags) =>
        new(1, "bank", center, new(1f), 0f, tags, true, WorldMapCollision.Solid);
}
