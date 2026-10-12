using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>A north-up map widget drawn inside the caller's already-open UI sprite batch.</summary>
/// <remarks>Update and draw on the surface's frame thread. The caller owns the shared cache and any chunk worker.</remarks>
public sealed class WorldMapView : IDisposable
{
    readonly WorldMapViewCore<Texture2D> _core;
    readonly Texture2D _white;
    readonly PrimitiveRenderer _primitives;
    bool _disposed;

    /// <summary>Creates a view with its own uploaded chunk textures and white placeholder pixel.</summary>
    /// <param name="cache">The shared map cache, whose lifetime remains with the caller.</param>
    /// <param name="surface">The surface that creates this view's textures.</param>
    public WorldMapView(WorldMapChunkCache cache, Render2DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _core = new WorldMapViewCore<Texture2D>(cache, surface.CreateTexture, static texture => texture.Dispose());
        _white = Render2DTextures.White(surface);
        _primitives = new PrimitiveRenderer(_white);
    }

    /// <summary>The viewport in the same coordinate space as the pointer and the caller's open batch.</summary>
    public Rect Viewport { get => _core.Viewport; set => _core.Viewport = value; }

    /// <summary>The finite positive maximum zoom in screen pixels per map metre. Defaults to eight.</summary>
    public float MaxPixelsPerMetre { get => _core.MaxPixelsPerMetre; set => _core.MaxPixelsPerMetre = value; }

    /// <summary>The maximum chunk uploads per update. Defaults to four. Zero pauses uploads.</summary>
    public int UploadBudgetPerFrame { get => _core.UploadBudgetPerFrame; set => _core.UploadBudgetPerFrame = value; }

    /// <summary>The colour used for chunks without uploaded imagery. Defaults to the cache style's void colour.</summary>
    public Color PlaceholderColor { get => _core.PlaceholderColor; set => _core.PlaceholderColor = value; }

    /// <summary>The current zoom in screen pixels per map metre.</summary>
    public float PixelsPerMetre => _core.PixelsPerMetre;

    /// <summary>Converts east/north map metres to the caller's UI coordinates.</summary>
    /// <param name="mapPosition">The map position in metres.</param>
    public Vector2 MapToScreen(Vector2 mapPosition) => _core.MapToScreen(mapPosition);

    /// <summary>Converts the caller's UI coordinates to east/north map metres.</summary>
    /// <param name="screenPosition">The position in the viewport's coordinate space.</param>
    public Vector2 ScreenToMap(Vector2 screenPosition) => _core.ScreenToMap(screenPosition);

    /// <summary>Centres on a map position, clamped to the authored bounds, without changing zoom.</summary>
    /// <param name="mapPosition">The map position in metres.</param>
    public void CenterOn(Vector2 mapPosition) => _core.CenterOn(mapPosition);

    /// <summary>Multiplies zoom about the viewport centre, clamped between whole-world fit and maximum zoom.</summary>
    /// <param name="factor">A finite positive zoom multiplier.</param>
    public void ZoomBy(float factor) => _core.ZoomBy(factor);

    /// <summary>Centres and fits the authored world. Does nothing until the viewport is at least one pixel per axis.</summary>
    public void FitWorld() => _core.FitWorld();

    /// <summary>Handles drag and cursor-centred wheel zoom, requests visible chunks and budgets texture uploads.</summary>
    /// <param name="pointer">This frame's pointer in the viewport's coordinate space.</param>
    /// <param name="wheelDelta">This frame's wheel delta.</param>
    public void Update(Pointer pointer, float wheelDelta) => _core.Update(pointer, wheelDelta);

    /// <summary>Draws the latest clipped chunk list without opening, closing or changing the batch's scissor.</summary>
    /// <param name="batch">The caller's already-open UI sprite batch.</param>
    public void Draw(SpriteBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(batch);
        if (Viewport.Width < 1f || Viewport.Height < 1f) return;
        foreach (WorldMapDrawItem<Texture2D> item in _core.DrawList)
        {
            if (item.Texture is Texture2D texture)
                batch.Draw(texture, item.Destination, item.SourceUv, Color.White);
            else
                _primitives.DrawFilledRect(batch, item.Destination, PlaceholderColor);
        }
    }

    /// <summary>Disposes every texture created by this view, retaining the caller's cache and surface.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.Dispose();
        _primitives.Dispose();
        _white.Dispose();
    }
}
