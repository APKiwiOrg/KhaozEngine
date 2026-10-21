using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Filtered chunk edges read neighbouring map imagery rather than their own opposite edge.</summary>
[Collection("WorldMapSnapshotGpu")]
public sealed class WorldMapSeamGpuTests
{
    const int Size = 96;
    // Each chunk's far edges are red. Both sides of every seam inside the view are green, RGB 26,204,26.
    static readonly Color Near = new(0.1f, 0.8f, 0.1f);
    static readonly Color Far = new(0.9f, 0.1f, 0.1f);

    /// <summary>Four level-zero chunks meet in the view at a fractional zoom and sub-pixel pan.</summary>
    [WorldMapGpuFact]
    public void AdjacentChunks_FractionalZoom_KeepSeamsFreeOfFarEdgeColour()
    {
        var source = new FlatTestSource(new MapRect(0f, 0f, 64f, 64f)) { GroundAt = p => Ground(p, new(32f, 32f)) };
        using var cache = new WorldMapChunkCache(source, new WorldMapStyle { ChunkMetres = 32f, PixelsPerMetre = 2f });
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                cache.Request(new(x, y), 0f);
        while (cache.BuildNext()) { }

        AssertSeamless(cache, new Vector2(32.37f, 31.81f), new Vector2(32f, 32f), expectedLevel: 0, parentFallback: false);
    }

    /// <summary>Two adjacent parent textures stand in for missing level-zero chunks across their shared seam.</summary>
    [WorldMapGpuFact]
    public void ParentFallback_FractionalZoom_KeepsSeamFreeOfFarEdgeColour()
    {
        var source = new FlatTestSource(new MapRect(0f, 0f, 128f, 64f)) { GroundAt = p => Ground(p, new(64f, 32f)) };
        using var cache = new WorldMapChunkCache(source, new WorldMapStyle { ChunkMetres = 32f, PixelsPerMetre = 2f });
        cache.Request(new(0, 0, 1), 0f);
        cache.Request(new(1, 0, 1), 0f);
        while (cache.BuildNext()) { }

        AssertSeamless(cache, new Vector2(64.37f, 31.81f), new Vector2(64f, float.NaN), expectedLevel: 0, parentFallback: true);
    }

    static void AssertSeamless(WorldMapChunkCache cache, Vector2 centre, Vector2 seams, int expectedLevel, bool parentFallback)
    {
        Vector2 screenSeams = default;
        int drawn = 0;
        byte[] rgba = Render2DSnapshot.Capture(Size, Size, Color.Black, context =>
        {
            using var view = new WorldMapView(cache, context) { Viewport = new Rect(0f, 0f, Size, Size) };
            view.CenterOn(centre);
            view.ZoomBy(6.7f / view.PixelsPerMetre);
            view.CenterOn(centre);
            view.Update(new TestInput().At(Vector2.Zero), 0f);
            Assert.Equal(expectedLevel, view.CurrentLevel);
            Assert.Equal(6.7f, view.PixelsPerMetre, 4);
            Assert.Equal(parentFallback, !cache.TryGet(new(1, 0), out _));
            screenSeams = view.MapToScreen(new Vector2(seams.X, float.IsNaN(seams.Y) ? centre.Y : seams.Y));
            context.Batch.Begin();
            view.Draw(context.Batch);
            context.Batch.End();
            drawn++;
        });

        Assert.Equal(1, drawn);
        Assert.Equal(Size * Size * 4, rgba.Length);
        for (int row = 0; row < Size; row++)
            for (int column = 0; column < Size; column++)
            {
                int index = (row * Size + column) * 4;
                Assert.True(rgba[index] <= 32 && rgba[index + 1] >= 196,
                    $"Pixel ({column}, {row}) is ({rgba[index]}, {rgba[index + 1]}, {rgba[index + 2]}), " +
                    $"seams at {screenSeams}: a chunk edge sampled its far edge.");
            }
        // The seam columns and rows themselves were inside the view and so covered by the loop.
        Assert.InRange(screenSeams.X, 8f, Size - 8f);
        if (!float.IsNaN(seams.Y)) Assert.InRange(screenSeams.Y, 8f, Size - 8f);
    }

    static Color Ground(Vector2 point, Vector2 seams) =>
        MathF.Abs(point.X - seams.X) < 13f && MathF.Abs(point.Y - seams.Y) < 13f ? Near : Far;
}
