using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Proves map orientation, viewport offsets and navigation using real input frames.</summary>
public sealed class WorldMapViewNavigationTests
{
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly Vector2 Center = new(128f, 96f);

    /// <summary>The map's north is up, east is right and the viewport offset is preserved.</summary>
    [Fact]
    public void MapToScreen_NorthUpEastRight_IncludesViewportOffset()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        view.CenterOn(Center);

        Assert.Equal(new Vector2(300f, 200f), view.MapToScreen(Center));
        Vector2 east = view.MapToScreen(new Vector2(138f, 96f));
        Vector2 north = view.MapToScreen(new Vector2(128f, 106f));
        Assert.True(east.X > 300f);
        Assert.Equal(200f, east.Y);
        Assert.Equal(300f, north.X);
        Assert.True(north.Y < 200f);
    }

    /// <summary>The transforms invert one another at a non-origin viewport.</summary>
    [Fact]
    public void ScreenToMap_InvertsMapToScreen()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        view.CenterOn(Center);
        var point = new Vector2(73.25f, 122.5f);

        Assert.True(Vector2.Distance(point, view.ScreenToMap(view.MapToScreen(point))) < 0.001f);
    }

    /// <summary>Repeated wheel steps respect both the whole-world fit and eight pixels per metre.</summary>
    [Fact]
    public void Zoom_ClampsBetweenWorldFitAndMaxPixelsPerMetre()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();
        var cursor = new Vector2(300f, 200f);
        for (int step = 0; step < 50; step++) view.Update(input.At(cursor), 1f);
        Assert.Equal(8f, view.PixelsPerMetre);
        for (int step = 0; step < 50; step++) view.Update(input.At(cursor), -1f);
        Assert.Equal(MathF.Min(400f / 256f, 300f / 192f), view.PixelsPerMetre);

        view.ZoomBy(100f);
        Assert.Equal(8f, view.PixelsPerMetre);
        view.ZoomBy(0.001f);
        Assert.Equal(1.5625f, view.PixelsPerMetre);
    }

    /// <summary>A wheel step preserves the map point under the cursor.</summary>
    [Fact]
    public void Wheel_KeepsMapPointUnderCursor()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();
        var cursor = new Vector2(180f, 120f);
        view.CenterOn(Center);
        view.Update(input.At(cursor), 0f);
        Vector2 before = view.ScreenToMap(cursor);

        view.Update(input.At(cursor), 1f);

        Assert.True(Vector2.Distance(before, view.ScreenToMap(cursor)) < 0.01f);
    }

    /// <summary>Held input pans by screen delta divided by current zoom.</summary>
    [Fact]
    public void Drag_PansTheMap()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();
        var start = new Vector2(300f, 200f);
        view.CenterOn(Center);
        view.Update(input.At(start), 0f);
        view.Update(input.At(start, down: true), 0f);

        view.Update(input.At(new Vector2(340f, 220f), down: true), 0f);

        Assert.Equal(new Vector2(340f, 220f), view.MapToScreen(Center));
        Assert.True(input.Pointer.IsBlocked(start));
    }

    /// <summary>An unusable viewport neither queues work nor blocks input.</summary>
    [Fact]
    public void ZeroSizeViewport_RequestsNothing_DoesNotThrow()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();
        foreach (Rect viewport in new Rect[] { new(100, 50, 0, 300), new(100, 50, 400, 0), new(100, 50, 0.5f, 300) })
        {
            view.Viewport = viewport;
            view.Update(input.At(new Vector2(100f, 50f)), 1f);
            Assert.Empty(view.DrawList);
            Assert.False(input.Pointer.IsBlocked(new Vector2(100f, 50f)));
        }
        Assert.False(cache.BuildNext());
    }

    /// <summary>Automatic fitting occurs once and explicit pre-update positioning takes precedence.</summary>
    [Fact]
    public void FirstUpdate_FitsWorld_UnlessCenteredFirst()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var input = new TestInput();
        using var fitted = View(cache);
        fitted.Viewport = default;
        fitted.Update(input.At(new Vector2(300f, 200f)), 0f);
        fitted.Viewport = Viewport;
        fitted.Update(input.At(new Vector2(300f, 200f)), 0f);
        Assert.Equal(1.5625f, fitted.PixelsPerMetre);
        Assert.Equal(new Vector2(300f, 200f), fitted.MapToScreen(Center));

        using var centered = View(cache);
        centered.CenterOn(new Vector2(96f, 96f));
        float positionedZoom = centered.PixelsPerMetre;
        centered.Update(input.At(new Vector2(300f, 200f)), 0f);
        Assert.Equal(positionedZoom, centered.PixelsPerMetre);
        Assert.Equal(new Vector2(300f, 200f), centered.MapToScreen(new Vector2(96f, 96f)));

        using var zoomed = View(cache);
        zoomed.ZoomBy(2f);
        zoomed.Update(input.At(new Vector2(300f, 200f)), 0f);
        Assert.Equal(8f, zoomed.PixelsPerMetre);
        zoomed.FitWorld();
        Assert.Equal(1.5625f, zoomed.PixelsPerMetre);
    }

    /// <summary>A small offset world stays centred at capped fit zoom and clips imagery to its authored bounds.</summary>
    [Fact]
    public void SmallWorld_FitCappedAtMax_CentredAndClipped()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(16f, 24f, 32f, 16f)), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();

        view.Update(input.At(new Vector2(300f, 200f)), 0f);

        Assert.Equal(8f, view.PixelsPerMetre);
        Assert.Equal(new Vector2(300f, 200f), view.MapToScreen(new Vector2(32f, 32f)));
        Assert.Equal(new Vector2(172f, 136f), view.MapToScreen(new Vector2(16f, 40f)));
        Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
        view.Update(input.At(new Vector2(300f, 200f)), 0f);
        var item = Assert.Single(view.DrawList);
        Assert.NotNull(item.Texture);
        Assert.Equal(new Rect(172f, 136f, 256f, 128f), item.Destination);
        Assert.Equal(new Vector4(0.25f, 0.375f, 0.75f, 0.625f), item.SourceUv);
    }

    /// <summary>Empty authored bounds allow navigation without queuing work or drawing chunks.</summary>
    [Fact]
    public void EmptyWorld_NoRequestsNoThrow()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(16f, 24f, 0f, 0f)), new WorldMapStyle());
        using var view = View(cache);
        var input = new TestInput();

        view.Update(input.At(new Vector2(300f, 200f)), 0f);
        view.FitWorld();
        view.CenterOn(new Vector2(16f, 24f));
        view.ZoomBy(2f);
        view.Update(input.At(new Vector2(300f, 200f)), 1f);

        Assert.False(cache.BuildNext());
        Assert.Empty(view.DrawList);
        Assert.True(float.IsFinite(view.PixelsPerMetre));
        Assert.Equal(0, view.CurrentLevel);
    }

    static WorldMapViewCore<FakeTexture> View(WorldMapChunkCache cache) =>
        new(cache, (rgba, width, height) => new FakeTexture(rgba, width, height), texture => texture.Dispose())
        { Viewport = Viewport };
}
