using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>Headless navigation, chunk scheduling and texture upload orchestration for the map view.</summary>
internal sealed class WorldMapViewCore<TTexture> : IDisposable where TTexture : class
{
    readonly WorldMapChunkCache _cache;
    readonly PannableCanvas _canvas = new() { Wheel = CanvasWheelMode.Zoom };
    readonly ChunkTextureStore<TTexture> _textures;
    readonly List<VisibleChunk> _visibleChunks = [];
    readonly List<WorldMapDrawItem<TTexture>> _drawList = [];
    float _maxPixelsPerMetre = 8f;
    int _uploadBudgetPerFrame = 4;
    bool _positioned;
    bool _disposed;

    internal WorldMapViewCore(WorldMapChunkCache cache, Func<byte[], int, int, TTexture> createTexture,
        Action<TTexture> disposeTexture)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _textures = new ChunkTextureStore<TTexture>(createTexture, disposeTexture);
        PlaceholderColor = cache.Style.VoidColor;
        DrawList = _drawList.AsReadOnly();
        _canvas.Camera.Zoom = cache.Style.PixelsPerMetre;
        ConfigureCanvas();
        _canvas.CenterContent();
    }

    internal Rect Viewport { get => _canvas.Viewport; set => _canvas.Viewport = value; }

    internal float MaxPixelsPerMetre
    {
        get => _maxPixelsPerMetre;
        set
        {
            if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(nameof(value));
            _maxPixelsPerMetre = value;
            ConfigureCanvas();
            _canvas.CenterOn(_canvas.Camera.Position);
        }
    }

    internal int UploadBudgetPerFrame
    {
        get => _uploadBudgetPerFrame;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _uploadBudgetPerFrame = value;
        }
    }

    internal Color PlaceholderColor { get; set; }
    internal float PixelsPerMetre => _canvas.Camera.Zoom;
    internal IReadOnlyList<WorldMapDrawItem<TTexture>> DrawList { get; }

    internal Vector2 MapToScreen(Vector2 mapPosition) => _canvas.WorldToScreen(ToCanvas(mapPosition));
    internal Vector2 ScreenToMap(Vector2 screenPosition) => ToCanvas(_canvas.ScreenToWorld(screenPosition));

    internal void CenterOn(Vector2 mapPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _positioned = true;
        ConfigureCanvas();
        _canvas.CenterOn(ToCanvas(mapPosition));
    }

    internal void ZoomBy(float factor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(factor) || factor <= 0f) throw new ArgumentOutOfRangeException(nameof(factor));
        _positioned = true;
        ConfigureCanvas();
        _canvas.Camera.Zoom = Math.Clamp(_canvas.Camera.Zoom * factor, _canvas.MinZoom, _canvas.MaxZoom);
        // The canvas exposes no public clamp. Re-centering composes its existing clamp without a Gui change.
        _canvas.CenterOn(_canvas.Camera.Position);
    }

    internal void FitWorld()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!UsableViewport) return;
        _positioned = true;
        ConfigureCanvas();
        _canvas.Focus(_canvas.ContentBounds);
    }

    internal void Update(Pointer pointer, float wheelDelta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!UsableViewport) return;
        ArgumentNullException.ThrowIfNull(pointer);
        ConfigureCanvas();
        if (!_positioned) FitWorld();
        _canvas.Update(pointer, wheelDelta);
        RequestVisibleChunks();
        UploadVisibleChunks();
        RebuildDrawList();
    }

    bool UsableViewport => Viewport.Width >= 1f && Viewport.Height >= 1f;

    void ConfigureCanvas()
    {
        MapRect bounds = _cache.Source.Bounds;
        _canvas.ContentBounds = new Rect(bounds.X, -bounds.Top, bounds.Width, bounds.Height);
        _canvas.MaxZoom = MaxPixelsPerMetre;
        float fit = UsableViewport && bounds.Width > 0f && bounds.Height > 0f
            ? MathF.Min(Viewport.Width / bounds.Width, Viewport.Height / bounds.Height)
            : UsableViewport ? MaxPixelsPerMetre : 0.0001f;
        _canvas.MinZoom = MathF.Min(fit, MaxPixelsPerMetre);
        _canvas.Camera.Zoom = Math.Clamp(_canvas.Camera.Zoom, _canvas.MinZoom, _canvas.MaxZoom);
    }

    void RequestVisibleChunks()
    {
        _visibleChunks.Clear();
        Vector2 northWest = ScreenToMap(new Vector2(Viewport.X, Viewport.Y));
        Vector2 southEast = ScreenToMap(new Vector2(Viewport.Right, Viewport.Bottom));
        MapRect bounds = _cache.Source.Bounds;
        // Letterboxing and floating-point roundoff at the world edge must not create outside-world chunks.
        float left = MathF.Max(northWest.X, bounds.X);
        float bottom = MathF.Max(southEast.Y, bounds.Y);
        var visible = new MapRect(left, bottom,
            MathF.Min(southEast.X, bounds.Right) - left, MathF.Min(northWest.Y, bounds.Top) - bottom);
        Vector2 center = ToCanvas(_canvas.Camera.Position);
        foreach (WorldMapChunkCoord coord in WorldMapChunking.ChunksTouching(visible, _cache.Style.ChunkMetres))
        {
            MapRect chunk = WorldMapChunking.ChunkRect(coord, _cache.Style.ChunkMetres);
            float priority = Vector2.Distance(chunk.Center, center);
            _cache.Request(coord, priority);
            _visibleChunks.Add(new VisibleChunk(coord, priority));
        }
        _visibleChunks.Sort(static (a, b) =>
        {
            int priority = a.Priority.CompareTo(b.Priority);
            if (priority != 0) return priority;
            int row = a.Coord.Y.CompareTo(b.Coord.Y);
            return row != 0 ? row : a.Coord.X.CompareTo(b.Coord.X);
        });
    }

    void UploadVisibleChunks()
    {
        int uploaded = 0;
        foreach (VisibleChunk visible in _visibleChunks)
        {
            if (uploaded >= UploadBudgetPerFrame) break;
            if (_cache.TryGet(visible.Coord, out WorldMapChunk? built) && _textures.Upload(built)) uploaded++;
        }
    }

    void RebuildDrawList()
    {
        _drawList.Clear();
        foreach (VisibleChunk visible in _visibleChunks)
        {
            MapRect chunk = WorldMapChunking.ChunkRect(visible.Coord, _cache.Style.ChunkMetres);
            Vector2 topLeft = MapToScreen(new Vector2(chunk.X, chunk.Top));
            var destination = new Rect(topLeft.X, topLeft.Y, chunk.Width * PixelsPerMetre, chunk.Height * PixelsPerMetre);
            if (WorldMapDrawList.TryClip(_textures.Get(visible.Coord), destination, Viewport, out var item))
                _drawList.Add(item);
        }
    }

    static Vector2 ToCanvas(Vector2 position) => new(position.X, -position.Y);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textures.Dispose();
        _visibleChunks.Clear();
        _drawList.Clear();
    }

    readonly record struct VisibleChunk(WorldMapChunkCoord Coord, float Priority);
}
