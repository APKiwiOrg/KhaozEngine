using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

[Collection("AllocSensitive")]
public sealed class AuthoredPlacementBufferTests
{
    [Fact]
    public void PrepareAppliesVisibilityAndKeepsFirstSelectedDuplicateOutOfDrawList()
    {
        var placements = new List<EditorPlacement>
        {
            Placement("a", "tree", 1f),
            Placement("selected", "rock", 2f),
            Placement("hidden", "tree", 3f),
            Placement("selected", "tree", 4f),
            Placement("category-hidden", "building", 5f),
        };
        var visibility = new EditorVisibility();
        visibility.SetElementHidden(SelectionKind.Placement, "hidden", true);
        var buffer = new AuthoredPlacementBuffer();

        buffer.Prepare(placements, visibility, static kind => kind != "building", "selected");

        Assert.Equal(2, buffer.Unselected.Count);
        Assert.Equal(placements[0].Prop, buffer.Unselected[0]);
        Assert.Equal(placements[3].Prop, buffer.Unselected[1]);
        Assert.Equal(placements[1], buffer.Selected);
    }

    [Fact]
    public void PrepareRefillsForShowAllAndShrinkingSourcesWithoutStaleEntries()
    {
        var placements = new List<EditorPlacement>
        {
            Placement("a", "tree", 1f),
            Placement("b", "rock", 2f),
            Placement("c", "building", 3f),
        };
        var visibility = new EditorVisibility();
        var buffer = new AuthoredPlacementBuffer();

        visibility.SetGroup(VisibilityGroup.Placements, false);
        buffer.Prepare(placements, visibility, static _ => true, "b");
        Assert.Empty(buffer.Unselected);
        Assert.Null(buffer.Selected);

        visibility.ShowAll();
        buffer.Prepare(placements, visibility, static _ => true, null);
        Assert.Equal(placements.ConvertAll(static placement => placement.Prop), buffer.Unselected);

        buffer.Prepare(placements.GetRange(0, 1), visibility, static _ => true, null);
        Assert.Single(buffer.Unselected);
        Assert.Equal(placements[0].Prop, buffer.Unselected[0]);

        buffer.Prepare(Array.Empty<EditorPlacement>(), visibility, static _ => true, null);
        Assert.Empty(buffer.Unselected);
        Assert.Null(buffer.Selected);
    }

    [Fact]
    public void WarmPrepareAllocatesZeroBytesAcrossCombinedFilteringPartitionAndProjection()
    {
        var placements = new List<EditorPlacement>(4096);
        for (int i = 0; i < placements.Capacity; i++)
            placements.Add(Placement(i.ToString(), (i & 1) == 0 ? "tree" : "rock", i));
        var visibility = new EditorVisibility();
        var buffer = new AuthoredPlacementBuffer();
        Func<string, bool> kindVisible = static _ => true;
        buffer.Prepare(placements, visibility, kindVisible, "2048");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            buffer.Prepare(placements, visibility, kindVisible, "2048");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(4095, buffer.Unselected.Count);
        Assert.Equal("2048", buffer.Selected?.Id);
    }

    [Fact]
    public void TerrainOnlyDefersDirtyCacheUntilRevealAndThenUsesCurrentField()
    {
        var document = new MapDocument
        {
            Id = "terrain-only-cache",
            Bounds = new MapBounds { MinX = -32f, MinZ = -32f, MaxX = 32f, MaxZ = 32f },
        };
        document.Placements.Add(new MapPlacement
        {
            Id = "grounded", Kind = "tree", X = 1f, Z = 2f, Y = null,
        });
        var cache = new PlacementCache();
        var buffer = new AuthoredPlacementBuffer();
        var visibility = new EditorVisibility { TerrainOnly = true };
        TerrainField highField = ConstantField(7f);

        buffer.Prepare(cache, document, ConstantField(1f), visibility, static _ => true, null);

        Assert.True(cache.IsDirty);
        Assert.Empty(buffer.Unselected);
        visibility.TerrainOnly = false;
        buffer.Prepare(cache, document, highField, visibility, static _ => true, null);
        Assert.False(cache.IsDirty);
        Assert.Single(buffer.Unselected);
        Assert.Equal(7f, buffer.Unselected[0].Y);
    }

    static EditorPlacement Placement(string id, string kind, float x) =>
        new(id, new PropPlacement(kind, x, x + 1f, x + 2f, x + 3f, x + 4f, (int)x));

    static TerrainField ConstantField(float height) => new(new TerrainConfig
    {
        GentleAmplitude = 0f,
        DetailOctaves = 0,
        Biomes = new[]
        {
            new BiomeBand
            {
                Start = float.NegativeInfinity,
                End = float.PositiveInfinity,
                Biome = BiomeId.Meadow,
                BaseHeight = height,
                HillAmplitude = 0f,
            },
        },
    });
}
