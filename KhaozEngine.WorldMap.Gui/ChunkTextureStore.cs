using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>Owns uploaded chunk textures independently of cache and worker lifetimes.</summary>
internal sealed class ChunkTextureStore<TTexture> : IDisposable where TTexture : class
{
    readonly Func<byte[], int, int, TTexture> _createTexture;
    readonly Action<TTexture> _disposeTexture;
    readonly Dictionary<WorldMapChunkCoord, UploadedChunk> _textures = [];
    long _frame;

    internal ChunkTextureStore(Func<byte[], int, int, TTexture> createTexture, Action<TTexture> disposeTexture)
    {
        ArgumentNullException.ThrowIfNull(createTexture);
        ArgumentNullException.ThrowIfNull(disposeTexture);
        _createTexture = createTexture;
        _disposeTexture = disposeTexture;
    }

    internal TTexture? Get(WorldMapChunkCoord coord) =>
        _textures.TryGetValue(coord, out UploadedChunk? uploaded) ? uploaded.Texture : null;

    internal void BeginFrame() => _frame++;

    internal void MarkDrawn(WorldMapChunkCoord coord)
    {
        if (_textures.TryGetValue(coord, out UploadedChunk? uploaded)) uploaded.LastDrawnFrame = _frame;
    }

    internal void Evict(int maxTextures)
    {
        while (_textures.Count > maxTextures)
        {
            WorldMapChunkCoord oldestCoord = default;
            UploadedChunk? oldest = null;
            foreach (var pair in _textures)
            {
                UploadedChunk uploaded = pair.Value;
                if (uploaded.LastDrawnFrame == _frame) continue;
                if (oldest is not null && uploaded.LastDrawnFrame >= oldest.LastDrawnFrame) continue;
                oldestCoord = pair.Key;
                oldest = uploaded;
            }
            // Draw items still borrow this frame's textures. Revisit the cap when they leave the view.
            if (oldest is null) break;
            _textures.Remove(oldestCoord);
            _disposeTexture(oldest.Texture);
        }
    }

    internal bool Upload(WorldMapChunk built)
    {
        WorldMapChunkImage image = built.Image;
        _textures.TryGetValue(image.Coord, out UploadedChunk? old);
        if (old?.Generation == built.Generation) return false;

        // Creation can fail. Keep the previous upload owned and drawable until a replacement exists.
        TTexture texture = _createTexture(image.Rgba, image.Size, image.Size);
        _textures[image.Coord] = new UploadedChunk(texture, built.Generation);
        if (old is not null) _disposeTexture(old.Texture);
        return true;
    }

    public void Dispose()
    {
        foreach (UploadedChunk uploaded in _textures.Values) _disposeTexture(uploaded.Texture);
        _textures.Clear();
    }

    sealed record UploadedChunk(TTexture Texture, long Generation)
    {
        internal long LastDrawnFrame { get; set; }
    }
}
