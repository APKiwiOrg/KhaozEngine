using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

public sealed class WorldMapViewOwnershipTests
{
    [Fact]
    public void Constructors_RejectNullBeforeCreatingTextures()
    {
        using var fixture = new WorldMapRenderFixture();
        using var cache = new WorldMapChunkCache(new FlatTestSource(), new WorldMapStyle());
        Assert.Equal("surface", Assert.Throws<ArgumentNullException>(() => new WorldMapView(cache, (Render2DSurface)null!)).ParamName);
        Assert.Equal("cache", Assert.Throws<ArgumentNullException>(() => new WorldMapView(null!, fixture.Surface)).ParamName);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => WorldMapRenderFixture.SnapshotView(cache, null)).ParamName);
        Assert.Equal("cache", Assert.Throws<ArgumentNullException>(() => WorldMapRenderFixture.SnapshotView(null, fixture.Context)).ParamName);
        Assert.Empty(fixture.Device.Textures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicAdapter_HonoursOwnershipDuringReplacementEvictionAndDisposal(bool snapshot)
    {
        using var fixture = new WorldMapRenderFixture();
        var source = new FlatTestSource(new MapRect(0f, 0f, 128f, 64f));
        source.Markers.Add(new WorldMapMarker("icon", new Vector2(12f, 16f), ["icon"]));
        source.Markers.Add(new WorldMapMarker("label", new Vector2(20f, 16f), ["label", "town"]));
        var style = new WorldMapStyle
        {
            ChunkMetres = 32f,
            PixelsPerMetre = 2f,
            IconRules = [new(WorldMapFeatureSource.Marker, ["icon"], "icon", 1)],
            Labels = new("label", [new("town", 1, 0f, 9f, 1f)])
        };
        using var cache = new WorldMapChunkCache(source, style);
        using Texture2D icon = fixture.Surface.CreateTexture([255, 0, 0, 255], 1, 1);
        using SpriteFont font = fixture.Surface.LoadDefaultFont(10f);
        MapGpuResource[] borrowed = fixture.Device.Textures.ToArray();
        using WorldMapView view = snapshot
            ? WorldMapRenderFixture.SnapshotView(cache, fixture.Context)
            : new WorldMapView(cache, fixture.Surface);
        MapGpuResource white = fixture.Device.Textures.Last();
        view.Viewport = new Rect(0f, 0f, 64f, 64f);
        view.CenterOn(new Vector2(16f, 16f));
        view.MaxTextures = 1;
        view.LabelFont = font;
        view.LabelText = _ => "A";
        view.IconSprites = _ => new WorldMapSprite(icon, new Vector4(0f, 0f, 1f, 1f), new Vector2(4f));
        view.SetMarker("player", new Vector2(16f, 24f), 0f,
            new WorldMapSprite(icon, new Vector4(0f, 0f, 1f, 1f), new Vector2(4f)));
        var input = new TestInput();
        view.Update(input.At(Vector2.Zero), 0f);
        fixture.BeginFrame();
        view.Draw(fixture.Batch);
        fixture.Batch.End();
        Assert.Equal(2, fixture.Batch.FrameStats.Quads);
        Assert.True(cache.BuildNext());
        Assert.False(cache.BuildNext());
        view.Update(input.At(Vector2.Zero), 0f);
        MapGpuResource first = fixture.Device.Textures.Last();
        Assert.NotSame(white, first);
        fixture.BeginFrame();
        view.Draw(fixture.Batch);
        fixture.Batch.End();
        Assert.Equal(5, fixture.Batch.FrameStats.Quads);

        source.Change(new MapRect(0f, 0f, 32f, 32f));
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.True(cache.BuildNext());
        view.Update(input.At(Vector2.Zero), 0f);
        MapGpuResource replacement = fixture.Device.Textures.Last();
        Assert.NotSame(first, replacement);
        Assert.Equal(snapshot ? 0 : 1, first.Disposals);

        view.CenterOn(new Vector2(48f, 16f));
        view.Update(input.At(Vector2.Zero), 0f);
        Assert.True(cache.BuildNext());
        view.Update(input.At(Vector2.Zero), 0f);
        MapGpuResource second = fixture.Device.Textures.Last();
        Assert.NotSame(replacement, second);
        Assert.Equal(snapshot ? 0 : 1, replacement.Disposals);
        Assert.Equal(0, second.Disposals);
        view.Dispose();
        view.Dispose();
        Assert.Equal(snapshot ? 0 : 1, second.Disposals);
        Assert.Equal(snapshot ? 0 : 1, white.Disposals);
        Assert.All(borrowed, resource => Assert.Equal(0, resource.Disposals));
        if (snapshot)
        {
            fixture.EndCapture();
            Assert.All(new[] { white, first, replacement, second }, resource => Assert.Equal(1, resource.Disposals));
            Assert.All(borrowed, resource => Assert.Equal(0, resource.Disposals));
        }
        cache.Invalidate(new MapRect(32f, 0f, 32f, 32f));
        Assert.True(cache.BuildNext());
        Assert.Throws<ObjectDisposedException>(() => view.Draw(fixture.Batch));
    }
}
