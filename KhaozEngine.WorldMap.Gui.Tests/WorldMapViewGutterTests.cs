using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Chunk draws upload the whole gutter texture and sample only the chunk interior.</summary>
/// <remarks>Parent fallback mapping is covered through <see cref="ChunkUv"/> in the level tests.</remarks>
public sealed class WorldMapViewGutterTests
{
    // Default-style chunks hold 256 interior pixels and a one-pixel gutter on each side.
    const int TextureSize = 258;
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly Vector2 ScreenCenter = new(300f, 200f);

    /// <summary>A fully visible chunk maps its whole destination onto the interior texels.</summary>
    [Fact]
    public void UploadedChunk_SamplesOnlyItsInterior()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(96f, 96f));
        view.ZoomBy(4f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        while (cache.BuildNext()) { }

        view.Update(input.At(ScreenCenter), 0f);

        var centre = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(textures.Created[0], centre.Texture);
        Assert.All(textures.Created, texture => { Assert.Equal(TextureSize, texture.Width); Assert.Equal(TextureSize, texture.Height); });
        ChunkUv.AssertInterior(new(0f, 0f, 1f, 1f), centre);
    }

    /// <summary>A viewport-clipped chunk keeps the interior mapping on the clipped edge.</summary>
    [Fact]
    public void ClippedChunk_ShrinksTheInteriorMapping()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.UploadBudgetPerFrame = 16;
        var input = new TestInput();
        view.CenterOn(new Vector2(82f, 96f));
        view.ZoomBy(4f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        while (cache.BuildNext()) { }

        view.Update(input.At(ScreenCenter), 0f);

        var west = Assert.Single(view.DrawList, item => item.Destination == new Rect(100f, 72f, 128f, 256f));
        Assert.Equal(TextureSize, Assert.IsType<FakeTexture>(west.Texture).Width);
        ChunkUv.AssertInterior(new(0.5f, 0f, 1f, 1f), west);
    }

    /// <summary>A borderless image keeps the identity mapping, while a gutter insets it by one texel.</summary>
    [Fact]
    public void TextureStore_MapsByEachUploadsBorder()
    {
        var textures = new FakeTextures();
        using var store = new ChunkTextureStore<FakeTexture>(textures.Create, texture => texture.Dispose());
        var features = new WorldMapChunkFeatures([], []);
        store.Upload(new WorldMapChunk(new WorldMapChunkImage(new(0, 0), 2, new byte[16], 0), features, 1));
        store.Upload(new WorldMapChunk(new WorldMapChunkImage(new(1, 0), 2, new byte[64], 0) { Border = 1 }, features, 2));

        Assert.Equal(2, textures.Created[0].Width);
        Assert.Equal(4, textures.Created[1].Width);
        Assert.Equal(4, textures.Created[1].Height);
        Assert.Equal(new Vector4(0.25f, 0.5f, 1f, 0.75f), store.TextureUv(new(0, 0), new(0.25f, 0.5f, 1f, 0.75f)));
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 0.75f), store.TextureUv(new(1, 0), new(0f, 0.5f, 1f, 1f)));
    }

    static WorldMapViewCore<FakeTexture> View(WorldMapChunkCache cache, FakeTextures textures) =>
        new(cache, textures.Create, texture => texture.Dispose()) { Viewport = Viewport };
}
