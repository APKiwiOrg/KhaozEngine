using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Raster crowns preserve the existing icon hover targets.</summary>
public sealed class SymbolSizeHoverTests
{
    /// <summary>Hover uses fixed icon bounds rather than the symbol's enlarged map-space crown.</summary>
    [Fact]
    public void SizedSymbol_HoverKeepsIconAnchorAndScreenBounds()
    {
        var anchor = new Vector2(96f, 96f);
        var source = new FlatTestSource();
        source.Footprints.Add(new(1, "tree", anchor, new(0.5f), 0f, ["tree"], false, WorldMapCollision.Solid));
        var symbol = new WorldMapSymbol(WorldMapSymbolKind.Tree, Color.White, Color.Black) { SizeMetres = new(20f) };
        var style = new WorldMapStyle
        {
            SymbolRules = [new(new(), symbol)],
            IconRules = [new(WorldMapFeatureSource.Footprint, ["tree"], "tree", 1)]
        };
        using var cache = new WorldMapChunkCache(source, style);
        var textures = new FakeTextures();
        using var view = new WorldMapViewCore<FakeTexture>(cache, textures.Create, texture => texture.Dispose())
        {
            Viewport = new(100f, 50f, 400f, 300f),
            IconSizes = _ => new Vector2(12f, 16f)
        };
        view.CenterOn(anchor);
        var input = new TestInput();
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.True(cache.BuildNext());
        view.Update(input.At(view.MapToScreen(anchor)), 0f);
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "tree", anchor), view.Hovered);
        Assert.Equal(new Vector2(12f, 16f), Assert.Single(view.OverlayPlacement.Icons).Size);

        view.Update(input.At(view.MapToScreen(anchor) + new Vector2(10f, 0f)), 0f);

        Assert.Null(view.Hovered);
    }
}
