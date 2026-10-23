using System.Numerics;
using KhaozEngine.Gpu.TestKit;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

[CollectionDefinition("WorldMapSnapshotGpu", DisableParallelization = true)]
public sealed class WorldMapSnapshotGpuCollection { }

public sealed class WorldMapGpuFactAttribute : FactAttribute
{
    public WorldMapGpuFactAttribute() => Skip = GpuTestGate.SkipReason();
}

[Collection("WorldMapSnapshotGpu")]
public sealed class WorldMapViewSnapshotGoldenTests
{
    // Earlier draws name evicted chunks and the placeholder pixel after view disposal.
    [WorldMapGpuFact]
    public void Capture_SubmitsDrawsAfterEvictionAndViewDisposal()
    {
        var source = new FlatTestSource(new MapRect(0f, 0f, 128f, 64f));
        var style = new WorldMapStyle { ChunkMetres = 32f, PixelsPerMetre = 2f };
        using var cache = new WorldMapChunkCache(source, style);
        cache.Request(new WorldMapChunkCoord(0, 0), 0f);
        Assert.True(cache.BuildNext());
        source.GroundColor = Color.FromHex("#af7050");
        cache.Request(new WorldMapChunkCoord(1, 0), 0f);
        Assert.True(cache.BuildNext());
        byte[] rgba = Render2DSnapshot.Capture(128, 64, Color.Black, context =>
        {
            using var view = new WorldMapView(cache, context);
            Texture2D icon = context.CreateTexture([255, 0, 0, 255], 1, 1);
            SpriteFont font = context.LoadDefaultFont(10f);
            view.LabelFont = font;
            view.IconSprites = _ => new WorldMapSprite(icon, new Vector4(0f, 0f, 1f, 1f), new Vector2(4f));
            view.Viewport = new Rect(0f, 0f, 64f, 64f);
            view.CenterOn(new Vector2(16f, 16f));
            view.MaxTextures = 1;
            view.UploadBudgetPerFrame = 0;
            var input = new TestInput();
            view.Update(input.At(Vector2.Zero), 0f);
            context.Batch.Begin();
            view.Draw(context.Batch);
            context.Batch.End();
            view.UploadBudgetPerFrame = 1;
            view.Update(input.At(Vector2.Zero), 0f);
            context.Batch.Begin();
            view.Draw(context.Batch);
            context.Batch.End();

            view.Viewport = new Rect(64f, 0f, 64f, 64f);
            view.CenterOn(new Vector2(48f, 16f));
            view.Update(input.At(Vector2.Zero), 0f);
            context.Batch.Begin();
            view.Draw(context.Batch);
            view.Dispose();
            context.Batch.Draw(icon, new Rect(60f, 28f, 8f, 8f), Color.White);
            context.Batch.DrawString(font, "A", new Vector2(2f, 2f), Color.White);
            context.Batch.End();
        });

        Assert.Equal(128 * 64 * 4, rgba.Length);
        int left = (32 * 128 + 8) * 4;
        int right = (32 * 128 + 120) * 4;
        int marker = (32 * 128 + 64) * 4;
        Assert.True(rgba[left + 1] > rgba[left] + 20, "The evicted first chunk must survive command submission.");
        Assert.True(rgba[right] > rgba[right + 1] + 30, "The disposed view's last chunk must survive command submission.");
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[marker..(marker + 4)]);
    }
}
