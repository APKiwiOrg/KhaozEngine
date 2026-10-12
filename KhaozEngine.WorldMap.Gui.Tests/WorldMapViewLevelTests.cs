using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Proves zoom-driven requests, coarser texture sampling and bounded texture ownership.</summary>
public sealed class WorldMapViewLevelTests
{
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly Vector2 ScreenCenter = new(300f, 200f);

    /// <summary>Whole-world fit chooses level one, while native and higher zoom choose level zero.</summary>
    [Fact]
    public void LevelChoice_FollowsZoom()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache, new FakeTextures());
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(1.5625f, view.PixelsPerMetre);
        Assert.Equal(1, view.CurrentLevel);
        Assert.Equal(4, view.DrawList.Count);
        Build(cache, 4);
        Assert.True(cache.IsCurrent(new(0, 0, 1)));
        Assert.False(cache.IsCurrent(new(0, 0)));

        view.ZoomBy(4f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(4f, view.PixelsPerMetre);
        Assert.Equal(0, view.CurrentLevel);
        view.ZoomBy(2f);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(8f, view.PixelsPerMetre);
        Assert.Equal(0, view.CurrentLevel);
    }

    /// <summary>Level selection uses the raster's rounded effective scale rather than its requested scale.</summary>
    [Fact]
    public void LevelChoice_UsesEffectiveRasterScale()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(0f, 0f, 512f, 384f)),
            new WorldMapStyle { PixelsPerMetre = 1.57f });
        using var view = View(cache, new FakeTextures());
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(1, view.CurrentLevel);

        view.ZoomBy(0.783f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);

        Assert.Equal(0, view.CurrentLevel);
    }

    /// <summary>A large fitted world queues only a handful of coarse chunks.</summary>
    [Fact]
    public void HugeWorldFit_RequestsAFewCoarseChunks()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(0f, 0f, 6400f, 6400f)), new WorldMapStyle());
        using var view = View(cache, new FakeTextures());
        view.Update(new TestInput().At(ScreenCenter), 0f);

        Assert.Equal(6, view.CurrentLevel);
        int maximum = WorldMapChunking.MaxLevel(cache.Source.Bounds, cache.Style.ChunkMetres);
        Assert.InRange(view.CurrentLevel, maximum - 1, maximum);
        Assert.Equal(4, view.DrawList.Count);
        Build(cache, 4);
        Assert.True(cache.IsCurrent(new(0, 0, 6)));
        Assert.False(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>A missing north-east child samples its uploaded parent's north-east quarter.</summary>
    [Fact]
    public void MissingChunk_DrawsCoarserSubRect()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(96f, 96f));
        view.ZoomBy(2f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(0, 0, 1)));
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture parent = Assert.Single(textures.Created);
        view.UploadBudgetPerFrame = 0;
        view.ZoomBy(2f);
        view.CenterOn(new Vector2(96f, 96f));

        view.Update(input.At(ScreenCenter), 0f);

        var child = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(parent, child.Texture);
        Assert.Equal(new Vector4(0.5f, 0f, 1f, 0.5f), child.SourceUv);
        var west = Assert.Single(view.DrawList, item => item.Destination == new Rect(100f, 72f, 72f, 256f));
        Assert.Same(parent, west.Texture);
        Assert.Equal(new Vector4(0.359375f, 0f, 0.5f, 0.5f), west.SourceUv);
        Assert.False(cache.TryGet(new(1, 1), out _));

        // The finer upload replaces the fallback on the next update that permits uploads.
        cache.Request(new(1, 1), -1f);
        Assert.True(cache.BuildNext());
        view.UploadBudgetPerFrame = 1;
        view.Update(input.At(ScreenCenter), 0f);
        child = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(textures.Created[1], child.Texture);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), child.SourceUv);
    }

    /// <summary>Negative child addresses sample the correct half-open north-up parent quadrant.</summary>
    [Theory]
    [InlineData(-96f, -96f, 0f, 0.5f, 0.5f, 1f)]
    [InlineData(-32f, -96f, 0.5f, 0.5f, 1f, 1f)]
    [InlineData(-96f, -32f, 0f, 0f, 0.5f, 0.5f)]
    [InlineData(-32f, -32f, 0.5f, 0f, 1f, 0.5f)]
    public void MissingChunk_NegativeQuadrantsUseNorthUpUvs(float x, float y, float left, float top, float right, float bottom)
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(-256f, -256f, 512f, 512f)), new WorldMapStyle());
        cache.Request(new(-1, -1, 1), 0f);
        Assert.True(cache.BuildNext());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(-64f, -64f));
        view.ZoomBy(2f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture parent = Assert.Single(textures.Created);
        view.UploadBudgetPerFrame = 0;
        view.ZoomBy(2f);
        view.CenterOn(new Vector2(x, y));

        view.Update(input.At(ScreenCenter), 0f);

        var child = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(parent, child.Texture);
        Assert.Equal(new Vector4(left, top, right, bottom), child.SourceUv);
    }

    /// <summary>A two-level fallback samples the matching quarter-width cell, including negative addresses.</summary>
    [Theory]
    [InlineData(160f, 96f, 0, 0, 2, 1, 0.5f, 0.5f, 0.75f, 0.75f)]
    [InlineData(-160f, -96f, -1, -1, -3, -2, 0.25f, 0.25f, 0.5f, 0.5f)]
    public void MissingChunk_LevelTwoAncestor_UsesExactNorthUpCell(float x, float y, int parentX, int parentY,
        int childX, int childY, float left, float top, float right, float bottom)
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(-512f, -512f, 1024f, 1024f)), new WorldMapStyle());
        var ancestorCoord = new WorldMapChunkCoord(parentX, parentY, 2);
        cache.Request(ancestorCoord, 0f);
        Assert.True(cache.BuildNext());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(parentX * 256f + 128f, parentY * 256f + 128f));
        view.ZoomBy(1f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture ancestor = Assert.Single(textures.Created);
        view.UploadBudgetPerFrame = 0;
        view.ZoomBy(4f);
        view.CenterOn(new Vector2(x, y));

        view.Update(input.At(ScreenCenter), 0f);

        Assert.Equal(0, view.CurrentLevel);
        Assert.False(cache.TryGet(new(childX, childY), out _));
        Assert.False(cache.TryGet(new(childX >> 1, childY >> 1, 1), out _));
        var child = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(ancestor, child.Texture);
        Assert.Equal(new Vector4(left, top, right, bottom), child.SourceUv);
        Assert.Single(textures.Created);
    }

    /// <summary>The closest uploaded ancestor wins even when a more distant ancestor is also retained.</summary>
    [Fact]
    public void MissingChunk_UsesNearestUploadedCoarserLevel()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        cache.Request(new(0, 0, 2), 0f);
        Assert.True(cache.BuildNext());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(128f, 96f));
        view.ZoomBy(2f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture grandparent = Assert.Single(textures.Created);
        Assert.All(view.DrawList, item => Assert.Same(grandparent, item.Texture));
        cache.Request(new(0, 0, 1), -1f);
        Assert.True(cache.BuildNext());
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture parent = textures.Created[1];
        view.UploadBudgetPerFrame = 0;
        view.ZoomBy(2f);
        view.CenterOn(new Vector2(96f, 96f));

        view.Update(input.At(ScreenCenter), 0f);

        var child = Assert.Single(view.DrawList, item => item.Destination == new Rect(172f, 72f, 256f, 256f));
        Assert.Same(parent, child.Texture);
        Assert.Equal(new Vector4(0.5f, 0f, 1f, 0.5f), child.SourceUv);
        Assert.Equal(0, grandparent.Disposals);
    }

    /// <summary>Current-level uploads take the budget before built coarser fallback textures.</summary>
    [Fact]
    public void UploadBudget_PrefersCurrentLevelBeforeCoarserFallback()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        cache.Request(new(0, 0, 1), 0f);
        cache.Request(new(1, 1), 1f);
        Build(cache, 2);
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.UploadBudgetPerFrame = 1;
        view.CenterOn(new Vector2(96f, 96f));
        var input = new TestInput();

        view.Update(input.At(ScreenCenter), 0f);

        Assert.True(cache.TryGet(new(1, 1), out var fine));
        Assert.Same(fine.Image.Rgba, Assert.Single(textures.Created).Rgba);
        Assert.Single(view.DrawList, item => item.Texture is not null);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(2, textures.Created.Count);
        Assert.True(cache.TryGet(new(0, 0, 1), out var parent));
        Assert.Same(parent.Image.Rgba, textures.Created[1].Rgba);
        Assert.Same(textures.Created[1], Assert.Single(view.DrawList,
            item => item.Destination == new Rect(100f, 328f, 72f, 22f)).Texture);
    }

    /// <summary>A four-texture cap evicts the two oldest draws after visiting six chunks and revisiting the first.</summary>
    [Fact]
    public void TextureCap_EvictsLeastRecentlyDrawn()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(new(0f, 0f, 512f, 64f)), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.MaxTextures = 4;
        view.ZoomBy(8f / view.PixelsPerMetre);
        var input = new TestInput();
        for (int column = 0; column < 6; column++)
        {
            if (column == 4)
            {
                view.CenterOn(new Vector2(32f, 32f));
                view.Update(input.At(ScreenCenter), 0f);
            }
            view.CenterOn(new Vector2(column * 64f + 32f, 32f));
            view.Update(input.At(ScreenCenter), 0f);
            Assert.True(cache.BuildNext());
            Assert.False(cache.BuildNext());
            view.Update(input.At(ScreenCenter), 0f);
            Assert.NotNull(Assert.Single(view.DrawList).Texture);
        }

        Assert.Equal(6, textures.Created.Count);
        Assert.Equal(4, textures.Created.Count(texture => texture.Disposals == 0));
        Assert.Equal(new[] { 0, 1, 1, 0, 0, 0 }, textures.Created.Select(texture => texture.Disposals));
        view.Dispose();
        Assert.All(textures.Created, texture => Assert.Equal(1, texture.Disposals));
    }

    /// <summary>The frame's drawn textures stay alive even when they alone exceed the cap.</summary>
    [Fact]
    public void TextureCap_ProtectsTexturesDrawnThisFrame()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.MaxTextures = 1;
        view.CenterOn(new Vector2(64f, 96f));
        view.ZoomBy(8f / view.PixelsPerMetre);
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Build(cache, 2);
        view.Update(input.At(ScreenCenter), 0f);

        Assert.Equal(2, view.DrawList.Count);
        Assert.All(view.DrawList, item => Assert.NotNull(item.Texture));
        Assert.All(textures.Created, texture => Assert.Equal(0, texture.Disposals));
        view.CenterOn(new Vector2(32f, 96f));
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(1, textures.Created.Count(texture => texture.Disposals == 0));
        Assert.Equal(0, Assert.Single(view.DrawList).Texture!.Disposals);
    }

    /// <summary>A drawn fallback survives eviction along with a fine texture drawn in the same frame.</summary>
    [Fact]
    public void TextureCap_ProtectsDrawnFallback()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        cache.Request(new(1, 1), 0f);
        cache.Request(new(0, 0, 1), 1f);
        Build(cache, 2);
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.MaxTextures = 1;
        view.CenterOn(new Vector2(96f, 96f));
        var input = new TestInput();

        view.Update(input.At(ScreenCenter), 0f);

        Assert.Equal(2, textures.Created.Count);
        Assert.Equal(4, view.DrawList.Count(item => item.Texture is not null));
        Assert.All(textures.Created, texture => Assert.Equal(0, texture.Disposals));
        view.ZoomBy(2f);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(1, textures.Created[1].Disposals);
        Assert.Equal(0, Assert.Single(view.DrawList).Texture!.Disposals);
    }

    /// <summary>The cap rejects zero and negative values without changing its current value.</summary>
    [Fact]
    public void TextureCap_InvalidValue_Throws()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache, new FakeTextures());

        Assert.Equal(192, view.MaxTextures);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.MaxTextures = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.MaxTextures = -1);
        Assert.Equal(192, view.MaxTextures);
    }

    static WorldMapViewCore<FakeTexture> View(WorldMapChunkCache cache, FakeTextures textures) =>
        new(cache, textures.Create, static texture => texture.Dispose()) { Viewport = Viewport };

    static void Build(WorldMapChunkCache cache, int count)
    {
        for (int chunk = 0; chunk < count; chunk++) Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
    }
}
