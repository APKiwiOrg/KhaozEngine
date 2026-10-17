using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Proves cached overlay anchors and live markers use the existing core's screen mapping.</summary>
public sealed class WorldMapViewOverlayTests
{
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly Vector2 Center = new(96f, 96f);
    static readonly Vector2 Anchor = new(96f, 100f);
    static readonly WorldMapLabelTier Tier = new("town", 1, 0f, 9f, 2f);

    /// <summary>Built feature anchors appear at the offset north-up screen position before texture uploads.</summary>
    [Fact]
    public void Icons_FromBuiltChunks_AppearAtMapToScreen()
    {
        var source = Source();
        source.Markers.Add(new WorldMapMarker("hidden", new Vector2(104f, 100f), ["hidden"]));
        using var cache = new WorldMapChunkCache(source, Style());
        using var view = View(cache);
        view.UploadBudgetPerFrame = 0;
        view.IconSizes = key => key == "bank" ? new Vector2(12f, 16f) : null;
        var input = new TestInput();
        BuildCenter(cache, view, input);

        ScreenIcon icon = Assert.Single(view.OverlayPlacement.Icons);
        Assert.Equal("bank", icon.Key);
        Assert.Equal(new Vector2(300f, 184f), icon.Position);
        Assert.Equal(view.MapToScreen(Anchor), icon.Position);
        Assert.Equal(new Vector2(12f, 16f), icon.Size);
        Assert.Equal(Anchor, icon.MapPosition);
        Assert.All(view.DrawList, item => Assert.Null(item.Texture));

        view.ZoomBy(2f);
        view.Update(input.At(Vector2.Zero), 0f);
        icon = Assert.Single(view.OverlayPlacement.Icons);
        Assert.Equal(new Vector2(300f, 168f), icon.Position);
        Assert.Equal(new Vector2(12f, 16f), icon.Size);
    }

    /// <summary>Only resolved labels appear, with measured screen dimensions scaled once by their tier.</summary>
    [Fact]
    public void Labels_ResolveThroughLabelText_NullHides()
    {
        var source = Source();
        source.Markers.Add(new WorldMapMarker("hidden", new Vector2(96f, 92f), ["label", "town"]));
        using var cache = new WorldMapChunkCache(source, Style());
        using var view = View(cache);
        view.LabelText = name => name == "village" ? "Localized village" : null;
        view.MeasureLabel = _ => new Vector2(20f, 10f);
        BuildCenter(cache, view, new TestInput());

        ScreenLabel label = Assert.Single(view.OverlayPlacement.Labels);
        Assert.Equal("village", label.MarkerName);
        Assert.Equal("Localized village", label.Text);
        Assert.Equal(new Vector2(300f, 184f), label.Position);
        Assert.Equal(new Vector2(40f, 20f), label.Size);
        Assert.Equal(Anchor, label.MapPosition);
    }

    /// <summary>Without a font measurer, resolved labels and their hover targets stay absent.</summary>
    [Fact]
    public void Labels_WithoutMeasurer_AreSkipped()
    {
        using var cache = new WorldMapChunkCache(Source(), Style());
        using var view = View(cache);
        view.LabelText = _ => "Localized village";
        BuildCenter(cache, view, new TestInput());

        Assert.Empty(view.OverlayPlacement.Labels);
    }

    /// <summary>A marker updates in place, retains clockwise heading and fixed size, and can be removed.</summary>
    [Fact]
    public void Marker_SetMoveRemove_FollowsMapToScreen()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), Style());
        using var view = View(cache);
        var input = new TestInput();
        view.SetMarker("player", Anchor, 0f, new Vector2(12f, 18f));
        view.Update(input.At(Vector2.Zero), 0f);
        var marker = Assert.Single(view.Markers);
        Assert.Equal("player", marker.Id);
        Assert.Equal(new Vector2(300f, 184f), marker.Position);
        Assert.Equal(new Vector2(12f, 18f), marker.Size);

        view.SetMarker("player", new Vector2(100f, 96f), 1.5707964f, new Vector2(16f, 20f));
        view.ZoomBy(2f);
        view.Update(input.At(Vector2.Zero), 0f);
        marker = Assert.Single(view.Markers);
        Assert.Equal(new Vector2(332f, 200f), marker.Position);
        Assert.Equal(new Vector2(16f, 20f), marker.Size);
        Assert.Equal(1.5707964f, marker.Heading);

        Assert.True(view.RemoveMarker("player"));
        Assert.False(view.RemoveMarker("player"));
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.Empty(view.Markers);
    }

    /// <summary>Only marker centres are clipped, using the viewport's half-open bounds.</summary>
    [Fact]
    public void Marker_OutsideViewport_IsSkipped()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), Style());
        using var view = View(cache);
        view.SetMarker("outside", new Vector2(40f, 96f), 0f, new Vector2(12f));
        view.SetMarker("right-edge", new Vector2(146f, 96f), 0f, new Vector2(12f));
        view.SetMarker("left-edge", new Vector2(46f, 96f), 0f, new Vector2(12f));

        view.Update(new TestInput().At(Vector2.Zero), 0f);

        var marker = Assert.Single(view.Markers);
        Assert.Equal("left-edge", marker.Id);
        Assert.Equal(new Vector2(100f, 200f), marker.Position);
    }

    /// <summary>Update reports icon identity and its original map anchor, then clears the old hover.</summary>
    [Fact]
    public void Hovered_ReportsIconUnderPointer()
    {
        using var cache = new WorldMapChunkCache(Source(), Style());
        using var view = View(cache);
        view.IconSizes = _ => new Vector2(12f, 16f);
        var input = new TestInput();
        BuildCenter(cache, view, input);

        view.Update(input.At(new Vector2(300f, 184f)), 0f);

        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", Anchor), view.Hovered);
        view.Update(input.At(new Vector2(400f, 300f)), 0f);
        Assert.Null(view.Hovered);
    }

    [Fact]
    public void HoverOverlay_NotRebuiltForOccludedPointer()
    {
        using var cache = new WorldMapChunkCache(Source(), Style());
        using var view = View(cache);
        view.IconSizes = _ => new Vector2(12f, 16f);
        var input = new TestInput();
        BuildCenter(cache, view, input);
        var pointer = input.At(new Vector2(300f, 184f));
        view.Update(pointer, 0f);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", Anchor), view.Hovered);

        pointer.Occlude(new Rect(294f, 176f, 12f, 16f));
        view.Update(pointer, 0f);
        Assert.Null(view.Hovered);
        Assert.Single(view.OverlayPlacement.Icons);

        pointer.ClearOcclusion();
        view.Update(pointer, 0f);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", Anchor), view.Hovered);
    }

    /// <summary>An unusable viewport clears overlay placement, marker draws and hover from the previous frame.</summary>
    [Fact]
    public void UnusableViewport_ClearsOverlaysAndHover()
    {
        using var cache = new WorldMapChunkCache(Source(), Style());
        using var view = View(cache);
        view.IconSizes = _ => new Vector2(12f, 16f);
        view.SetMarker("player", Center, 0f, new Vector2(12f));
        var input = new TestInput();
        BuildCenter(cache, view, input);
        view.Update(input.At(new Vector2(300f, 184f)), 0f);
        Assert.NotNull(view.Hovered);

        view.Viewport = default;
        view.Update(input.At(new Vector2(300f, 184f)), 0f);

        Assert.Null(view.Hovered);
        Assert.Empty(view.OverlayPlacement.Icons);
        Assert.Empty(view.OverlayPlacement.Labels);
        Assert.Empty(view.Markers);
    }

    /// <summary>An edge anchor belongs to one chunk and leaves the view when only that owner leaves.</summary>
    [Fact]
    public void BorderAnchor_DrawnOnce_AndLeavesWithItsOwner()
    {
        var source = new FlatTestSource();
        source.Markers.Add(new WorldMapMarker("border", new Vector2(64f, 96f), ["bank"]));
        using var cache = new WorldMapChunkCache(source, Style());
        using var view = View(cache);
        view.CenterOn(new Vector2(64f, 96f));
        view.ZoomBy(2f);
        int resolved = 0;
        view.IconSizes = _ => { resolved++; return new Vector2(12f, 16f); };
        var input = new TestInput();
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.Equal(2, view.DrawList.Count);
        Assert.True(cache.BuildNext());
        Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
        view.Update(input.At(Vector2.Zero), 0f);

        Assert.Equal(1, resolved);
        Assert.Equal(new Vector2(300f, 200f), Assert.Single(view.OverlayPlacement.Icons).Position);
        Assert.True(cache.TryGet(new(0, 1), out var west));
        Assert.Empty(west.Features.Icons);
        Assert.True(cache.TryGet(new(1, 1), out var east));
        Assert.Single(east.Features.Icons);

        view.CenterOn(new Vector2(32f, 96f));
        view.Update(input.At(Vector2.Zero), 0f);

        Assert.Single(view.DrawList);
        Assert.Empty(view.OverlayPlacement.Icons);
        Assert.Equal(1, resolved);
    }

    /// <summary>Fallback imagery does not contribute anchors from a different pyramid level.</summary>
    [Fact]
    public void CoarserFallback_UsesOnlyCurrentLevelAnchors()
    {
        using var cache = new WorldMapChunkCache(Source(), Style());
        cache.Request(new(0, 0, 1), 0f);
        Assert.True(cache.BuildNext());
        using var view = View(cache);
        view.IconSizes = _ => new Vector2(12f, 16f);
        view.ZoomBy(0.5f);
        var input = new TestInput();
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.Single(view.OverlayPlacement.Icons);
        view.UploadBudgetPerFrame = 0;
        view.ZoomBy(2f);

        view.Update(input.At(Vector2.Zero), 0f);

        Assert.Contains(view.DrawList, item => item.Texture is not null);
        Assert.Empty(view.OverlayPlacement.Icons);
    }

    static FlatTestSource Source()
    {
        var source = new FlatTestSource();
        source.Markers.Add(new WorldMapMarker("village", Anchor, ["bank", "label", "town"]));
        return source;
    }

    static WorldMapStyle Style() => new()
    {
        IconRules = [new WorldMapIconRule(WorldMapFeatureSource.Marker, ["bank"], "bank", 2, 24f),
            new WorldMapIconRule(WorldMapFeatureSource.Marker, ["hidden"], "hidden", 1, 24f)],
        Labels = new WorldMapLabelRules("label", [Tier])
    };

    static WorldMapViewCore<FakeTexture> View(WorldMapChunkCache cache)
    {
        var textures = new FakeTextures();
        var view = new WorldMapViewCore<FakeTexture>(cache, textures.Create, texture => texture.Dispose())
        { Viewport = Viewport };
        view.CenterOn(Center);
        return view;
    }

    static void BuildCenter(WorldMapChunkCache cache, WorldMapViewCore<FakeTexture> view, TestInput input)
    {
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.True(cache.BuildNext());
        view.Update(input.At(Vector2.Zero), 0f);
    }
}
