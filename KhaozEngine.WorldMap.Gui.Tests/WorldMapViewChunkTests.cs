using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Proves visible chunk scheduling, bounded uploads, clipping and texture ownership without a GPU.</summary>
public sealed class WorldMapViewChunkTests
{
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly Vector2 ScreenCenter = new(300f, 200f);

    /// <summary>Only visible chunks queue and the closest chunk builds and uploads first.</summary>
    [Fact]
    public void Update_RequestsVisibleChunks_CentreChunkBuildsFirst()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        view.CenterOn(new Vector2(96f, 96f));
        view.ZoomBy(4f / view.PixelsPerMetre);
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(9, view.DrawList.Count);

        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new WorldMapChunkCoord(1, 1)));
        Assert.False(cache.IsCurrent(new WorldMapChunkCoord(0, 0)));
        for (int chunk = 1; chunk < 9; chunk++) Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
        view.Update(input.At(ScreenCenter), 0f);
        Assert.True(cache.TryGet(new WorldMapChunkCoord(1, 1), out var center));
        Assert.Same(center.Image.Rgba, textures.Created[0].Rgba);
        Assert.False(cache.IsCurrent(new WorldMapChunkCoord(3, 1)));
    }

    /// <summary>Twelve built visible chunks upload in batches of four, without re-uploading unchanged generations.</summary>
    [Fact]
    public void Upload_RespectsBudgetPerFrame()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(12, view.DrawList.Count);
        Build(cache, 12);

        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(4, textures.Created.Count);
        Assert.Equal(4, view.DrawList.Count(item => item.Texture is not null));
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(8, textures.Created.Count);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(12, textures.Created.Count);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(12, textures.Created.Count);
        Assert.All(textures.Created, texture => { Assert.Equal(256, texture.Width); Assert.Equal(256, texture.Height); });
    }

    /// <summary>Invalidation and a zero upload budget preserve the old texture until one replacement uploads.</summary>
    [Fact]
    public void StaleChunk_KeepsOldTexture_UntilReplacementUploads()
    {
        var source = new FlatTestSource();
        using var cache = new WorldMapChunkCache(source, new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.CenterOn(new Vector2(96f, 96f));
        view.ZoomBy(8f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);
        Build(cache, 1);
        view.Update(input.At(ScreenCenter), 0f);
        FakeTexture old = Assert.Single(textures.Created);

        source.Change(new MapRect(96f, 96f, 1f, 1f));
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Same(old, Assert.Single(view.DrawList).Texture);
        Assert.Equal(0, old.Disposals);
        Assert.Single(textures.Created);
        Build(cache, 1);
        view.UploadBudgetPerFrame = 0;
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Same(old, Assert.Single(view.DrawList).Texture);

        view.UploadBudgetPerFrame = 4;
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(2, textures.Created.Count);
        Assert.Same(textures.Created[1], Assert.Single(view.DrawList).Texture);
        Assert.Equal(1, old.Disposals);
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Equal(2, textures.Created.Count);
        view.Dispose();
        Assert.All(textures.Created, texture => Assert.Equal(1, texture.Disposals));
    }

    /// <summary>Clipping a west-edge chunk preserves its sampled region and clips north-up UVs on all axes.</summary>
    [Fact]
    public void DrawList_ClipsToViewport_AndShrinksUv()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        using var view = View(cache, new FakeTextures());
        var input = new TestInput();
        view.CenterOn(new Vector2(82f, 96f));
        view.ZoomBy(4f / view.PixelsPerMetre);
        view.Update(input.At(ScreenCenter), 0f);

        var west = Assert.Single(view.DrawList, item => item.Destination.X == 100f && item.Destination.Y == 72f);
        Assert.Equal(0.5f, west.SourceUv.X, 3);
        Assert.Equal(128f, west.Destination.Width);
        Assert.Equal(new Vector4(0.5f, 0f, 1f, 1f), west.SourceUv);
        Assert.All(view.DrawList, item =>
        {
            Assert.True(item.Destination.X >= Viewport.X && item.Destination.Y >= Viewport.Y);
            Assert.True(item.Destination.Right <= Viewport.Right && item.Destination.Bottom <= Viewport.Bottom);
            Assert.True(item.SourceUv.X >= 0f && item.SourceUv.Y >= 0f);
            Assert.True(item.SourceUv.Z <= 1f && item.SourceUv.W <= 1f);
            Assert.True(item.SourceUv.X < item.SourceUv.Z && item.SourceUv.Y < item.SourceUv.W);
        });
        var north = Assert.Single(view.DrawList, item => item.Destination.X == 228f && item.Destination.Y == 50f);
        Assert.True(north.SourceUv.Y > 0f);
        Assert.Equal(1f, north.SourceUv.W);
    }

    /// <summary>Visible chunks with no upload are explicit placeholder draw items.</summary>
    [Fact]
    public void MissingChunk_IsPlaceholderItem()
    {
        var style = new WorldMapStyle { VoidColor = Color.FromHex("#102030") };
        using var cache = new WorldMapChunkCache(new FlatTestSource(), style);
        using var view = View(cache, new FakeTextures());
        view.Update(new TestInput().At(ScreenCenter), 0f);

        Assert.Equal(12, view.DrawList.Count);
        Assert.All(view.DrawList, item => Assert.Null(item.Texture));
        Assert.Equal(style.VoidColor, view.PlaceholderColor);
    }

    /// <summary>All uploads, including chunks no longer visible, are disposed exactly once without owning the cache.</summary>
    [Fact]
    public void Dispose_DisposesEveryCreatedTexture()
    {
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        var textures = new FakeTextures();
        using var view = View(cache, textures);
        var input = new TestInput();
        view.Update(input.At(ScreenCenter), 0f);
        Build(cache, 12);
        for (int frame = 0; frame < 3; frame++) view.Update(input.At(ScreenCenter), 0f);
        view.ZoomBy(8f / view.PixelsPerMetre);
        view.CenterOn(new Vector2(96f, 96f));
        view.Update(input.At(ScreenCenter), 0f);
        Assert.Single(view.DrawList);

        view.Dispose();
        view.Dispose();

        Assert.Equal(12, textures.Created.Count);
        Assert.All(textures.Created, texture => Assert.Equal(1, texture.Disposals));
        Assert.Empty(view.DrawList);
        cache.Invalidate(new MapRect(96f, 96f, 1f, 1f));
        Assert.True(cache.BuildNext());
    }

    static WorldMapViewCore<FakeTexture> View(WorldMapChunkCache cache, FakeTextures textures) =>
        new(cache, textures.Create, texture => texture.Dispose()) { Viewport = Viewport };

    static void Build(WorldMapChunkCache cache, int count)
    {
        for (int chunk = 0; chunk < count; chunk++) Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
    }
}
