using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>Headless navigation, chunk orchestration and screen-space overlays for the map view.</summary>
internal sealed class WorldMapViewCore<TTexture> : IDisposable where TTexture : class
{
    readonly WorldMapChunkCache _cache;
    readonly PannableCanvas _canvas = new() { Wheel = CanvasWheelMode.Zoom };
    readonly ChunkTextureStore<TTexture> _textures;
    readonly List<VisibleChunk> _visibleChunks = [];
    readonly List<WorldMapDrawItem<TTexture>> _drawList = [];
    readonly Dictionary<string, MapMarker> _markerStates = new(StringComparer.Ordinal);
    readonly List<string> _markerOrder = [];
    readonly List<ScreenMarker> _markers = [];
    readonly List<ScreenIcon> _icons = [];
    readonly List<ScreenLabel> _labels = [];
    readonly WorldMapOverlayBuffers _overlayBuffers = new();
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
        Markers = _markers.AsReadOnly();
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
    internal Func<string, Vector2?>? IconSizes { get; set; }
    internal Func<string, string?>? LabelText { get; set; }
    internal Func<string, Vector2>? MeasureLabel { get; set; }
    internal OverlayPlacement OverlayPlacement => _overlayBuffers.Placement;
    internal IReadOnlyList<ScreenMarker> Markers { get; }
    internal WorldMapHover? Hovered => OverlayPlacement.Hover;

    internal void SetMarker(string id, Vector2 mapPosition, float heading, Vector2 size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!_markerStates.ContainsKey(id))
            _markerOrder.Insert(~_markerOrder.BinarySearch(id, StringComparer.Ordinal), id);
        _markerStates[id] = new MapMarker(mapPosition, heading, size);
    }

    internal bool RemoveMarker(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_markerStates.Remove(id)) return false;
        _markerOrder.RemoveAt(_markerOrder.BinarySearch(id, StringComparer.Ordinal));
        return true;
    }

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
        if (!UsableViewport)
        {
            ClearOverlays();
            return;
        }
        ArgumentNullException.ThrowIfNull(pointer);
        ConfigureCanvas();
        if (!_positioned) FitWorld();
        _canvas.Update(pointer, wheelDelta);
        RequestVisibleChunks();
        UploadVisibleChunks();
        RebuildDrawList();
        RebuildOverlays(pointer.Position);
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

    internal void RebuildOverlays(Vector2 pointer)
    {
        _icons.Clear();
        _labels.Clear();
        foreach (VisibleChunk visible in _visibleChunks)
        {
            if (!_cache.TryGet(visible.Coord, out WorldMapChunk? built)) continue;
            for (int i = 0; i < built.Features.Icons.Count; i++)
            {
                WorldMapIconAnchor anchor = built.Features.Icons[i];
                if (IconSizes?.Invoke(anchor.IconKey) is not Vector2 size) continue;
                _icons.Add(new ScreenIcon(anchor.IconKey, MapToScreen(anchor.Position), size,
                    anchor.Priority, anchor.MergeRadiusPixels, anchor.Position));
            }
            if (MeasureLabel is not { } measure || LabelText is not { } resolve) continue;
            for (int i = 0; i < built.Features.Labels.Count; i++)
            {
                WorldMapLabelAnchor anchor = built.Features.Labels[i];
                if (PixelsPerMetre < anchor.Tier.MinPixelsPerMetre || PixelsPerMetre >= anchor.Tier.MaxPixelsPerMetre)
                    continue;
                if (resolve(anchor.MarkerName) is not string text) continue;
                _labels.Add(new ScreenLabel(anchor.MarkerName, text, MapToScreen(anchor.Position),
                    measure(text) * anchor.Tier.TextScale, anchor.Tier, anchor.Position));
            }
        }
        WorldMapOverlayLayout.Place(_icons, _labels, Viewport, PixelsPerMetre, pointer, _overlayBuffers);

        _markers.Clear();
        foreach (string id in _markerOrder)
        {
            MapMarker marker = _markerStates[id];
            Vector2 screen = MapToScreen(marker.Position);
            if (Viewport.Contains(screen)) _markers.Add(new ScreenMarker(id, screen, marker.Size, marker.Heading));
        }
    }

    void ClearOverlays()
    {
        _icons.Clear();
        _labels.Clear();
        _overlayBuffers.Clear();
        _markers.Clear();
    }

    static Vector2 ToCanvas(Vector2 position) => new(position.X, -position.Y);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textures.Dispose();
        _visibleChunks.Clear();
        _drawList.Clear();
        _markerStates.Clear();
        _markerOrder.Clear();
        ClearOverlays();
    }

    readonly record struct VisibleChunk(WorldMapChunkCoord Coord, float Priority);
    readonly record struct MapMarker(Vector2 Position, float Heading, Vector2 Size);
}
