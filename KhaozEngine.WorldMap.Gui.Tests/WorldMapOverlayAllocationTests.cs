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

        // Update also allocates the chunk traversal iterator. Measure only its production overlay entry point.
        long before = GC.GetAllocatedBytesForCurrentThread();
        view.RebuildOverlays(pointer.Position);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(4, view.OverlayPlacement.Icons.Count);
        Assert.Equal(4, view.OverlayPlacement.Labels.Count);
        Assert.Single(view.Markers);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", new Vector2(60f, 60f)), view.Hovered);
    }
}
