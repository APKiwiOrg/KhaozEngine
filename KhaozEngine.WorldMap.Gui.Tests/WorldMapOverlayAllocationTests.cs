using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Serializes allocation measurements against other tests in this assembly.</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }

/// <summary>Proves warmed overlay frames reuse their working storage.</summary>
[Collection("AllocSensitive")]
public sealed class WorldMapOverlayAllocationTests
{
    /// <summary>Warmed level selection and both native and coarser sampling allocate no frame storage.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void LevelSelectionAndFallback_WarmedUpdateAllocatesNothing(int level, bool fallback)
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        cache.Request(fallback || level == 1 ? new(0, 0, 1) : new(1, 1), 0f);
        Assert.True(cache.BuildNext());
        var textures = new FakeTextures();
        using var view = new WorldMapViewCore<FakeTexture>(cache, textures.Create, static texture => texture.Dispose())
        { Viewport = new Rect(100f, 50f, 400f, 300f) };
        view.CenterOn(new Vector2(96f, 96f));
        if (fallback || level == 1) view.ZoomBy(0.5f);
        Pointer pointer = new TestInput().At(new Vector2(300f, 200f));
        view.Update(pointer, 0f);
        view.UploadBudgetPerFrame = 0;
        if (fallback) view.ZoomBy(2f);
        view.Update(pointer, 0f);
        view.Update(pointer, 0f);
        Assert.Contains(view.DrawList, item => item.Texture is not null);

        long allocated = AllocationMeasurement.Measure("a warmed map view update", () => view.Update(pointer, 0f));

        Assert.Equal(0L, allocated);
        Assert.Equal(level, view.CurrentLevel);
    }

    /// <summary>Four built feature chunks and a hovered icon produce no allocations in the overlay pass.</summary>
    [Fact]
    public void OverlayPass_AfterTwoUpdates_AllocatesNothing()
    {
        var source = new FlatTestSource();
        source.Markers.Add(new WorldMapMarker("south-west", new Vector2(60f, 60f), ["bank", "label", "town"]));
        source.Markers.Add(new WorldMapMarker("south-east", new Vector2(100f, 60f), ["bank", "label", "town"]));
        source.Markers.Add(new WorldMapMarker("north-west", new Vector2(60f, 100f), ["bank", "label", "town"]));
        source.Markers.Add(new WorldMapMarker("north-east", new Vector2(100f, 100f), ["bank", "label", "town"]));
        var style = new WorldMapStyle
        {
            IconRules = [new WorldMapIconRule(WorldMapFeatureSource.Marker, ["bank"], "bank", 2, 24f)],
            Labels = new WorldMapLabelRules("label", [new WorldMapLabelTier("town", 1, 0f, 9f, 1f)])
        };
        using var cache = new WorldMapChunkCache(source, style);
        foreach (WorldMapChunkCoord coord in new WorldMapChunkCoord[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) })
        {
            cache.Request(coord, 0f);
            Assert.True(cache.BuildNext());
        }
        var textures = new FakeTextures();
        using var view = new WorldMapViewCore<FakeTexture>(cache, textures.Create, static texture => texture.Dispose())
        {
            Viewport = new Rect(100f, 50f, 400f, 400f),
            UploadBudgetPerFrame = 0,
            IconSizes = static _ => new Vector2(12f, 16f),
            LabelText = static name => name,
            MeasureLabel = static _ => new Vector2(20f, 10f)
        };
        view.CenterOn(new Vector2(96f, 96f));
        view.SetMarker("player", new Vector2(96f, 96f), 0f, new Vector2(12f));
        Pointer pointer = new TestInput().At(view.MapToScreen(new Vector2(60f, 60f)));
        view.Update(pointer, 0f);
        view.Update(pointer, 0f);
        Assert.Equal(4, view.OverlayPlacement.Icons.Count);
        Assert.Equal(4, view.OverlayPlacement.Labels.Count);
        Assert.Single(view.Markers);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", new Vector2(60f, 60f)), view.Hovered);

        // Isolate the overlay entry point. Whole-frame streaming is covered by the allocation test above.
        long allocated = AllocationMeasurement.Measure("a warmed overlay rebuild",
            () => view.RebuildOverlays(pointer.Position));

        Assert.Equal(0L, allocated);
        Assert.Equal(4, view.OverlayPlacement.Icons.Count);
        Assert.Equal(4, view.OverlayPlacement.Labels.Count);
        Assert.Single(view.Markers);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", new Vector2(60f, 60f)), view.Hovered);
    }
}
