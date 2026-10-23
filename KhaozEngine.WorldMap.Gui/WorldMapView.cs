using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>A north-up map widget drawn inside the caller's already-open UI sprite batch.</summary>
/// <remarks>Use on the rendering thread. The caller owns the shared cache and any chunk worker.</remarks>
public sealed class WorldMapView : IDisposable
{
    readonly WorldMapViewCore<Texture2D> _core;
    readonly Texture2D _white;
    readonly PrimitiveRenderer _primitives;
    readonly bool _ownsTextures;
    readonly Dictionary<string, WorldMapSprite> _markerSprites = new(StringComparer.Ordinal);
    SpriteFont? _labelFont;
    bool _disposed;

    /// <summary>Creates a view with its own uploaded chunk textures and white placeholder pixel.</summary>
    /// <param name="cache">The shared map cache, whose lifetime remains with the caller.</param>
    /// <param name="surface">The surface that creates this view's textures.</param>
    public WorldMapView(WorldMapChunkCache cache, Render2DSurface surface)
        : this(cache, (surface ?? throw new ArgumentNullException(nameof(surface))).CreateTexture, ownsTextures: true)
    { }

    /// <summary>Creates a view whose uploaded chunks and placeholder pixel belong to the snapshot capture.</summary>
    /// <param name="cache">The shared map cache, whose lifetime remains with the caller.</param>
    /// <param name="context">The capture context that retains textures until command submission finishes.</param>
    public WorldMapView(WorldMapChunkCache cache, Render2DContext context)
        : this(cache, (context ?? throw new ArgumentNullException(nameof(context))).CreateTexture, ownsTextures: false)
    { }

    WorldMapView(WorldMapChunkCache cache, Func<byte[], int, int, Texture2D> createTexture, bool ownsTextures)
    {
        _ownsTextures = ownsTextures;
        _core = new WorldMapViewCore<Texture2D>(cache, createTexture,
            ownsTextures ? static texture => texture.Dispose() : static _ => { });
        _core.IconSizes = key => IconSprites?.Invoke(key)?.Size;
        _white = createTexture([255, 255, 255, 255], 1, 1);
        _primitives = new PrimitiveRenderer(_white);
    }

    /// <summary>The viewport in the same coordinate space as the pointer and the caller's open batch.</summary>
    public Rect Viewport { get => _core.Viewport; set => _core.Viewport = value; }

    /// <summary>The finite positive maximum zoom in screen pixels per map metre. Defaults to eight.</summary>
    public float MaxPixelsPerMetre { get => _core.MaxPixelsPerMetre; set => _core.MaxPixelsPerMetre = value; }

    /// <summary>The maximum chunk uploads per update. Defaults to four. Zero pauses uploads.</summary>
    public int UploadBudgetPerFrame { get => _core.UploadBudgetPerFrame; set => _core.UploadBudgetPerFrame = value; }

    /// <summary>The positive texture retention cap, defaulting to 192. Textures drawn this frame remain protected.</summary>
    /// <remarks>Eviction runs during Update on the frame thread. Active draws can temporarily exceed the cap.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is below one.</exception>
    public int MaxTextures { get => _core.MaxTextures; set => _core.MaxTextures = value; }

    /// <summary>The pyramid level selected by the last update with a usable viewport. Zero is native resolution.</summary>
    public int CurrentLevel => _core.CurrentLevel;

    /// <summary>The colour used for chunks without uploaded imagery. Defaults to the cache style's void colour.</summary>
    public Color PlaceholderColor { get => _core.PlaceholderColor; set => _core.PlaceholderColor = value; }

    /// <summary>Resolves a borrowed icon sprite by key. A null callback or result hides the icon.</summary>
    public Func<string, WorldMapSprite?>? IconSprites { get; set; }

    /// <summary>Resolves localized label text by authored marker name. A null result hides the label.</summary>
    public Func<string, string?>? LabelText { get => _core.LabelText; set => _core.LabelText = value; }

    /// <summary>The caller-owned label font. With no font set, labels and their hover targets are skipped.</summary>
    public SpriteFont? LabelFont
    {
        get => _labelFont;
        set
        {
            _labelFont = value;
            _core.MeasureLabel = value is null ? null : text => value.Measure(text);
        }
    }

    /// <summary>The label text colour, defaulting to #2b2117.</summary>
    public Color LabelColor { get; set; } = Color.FromHex("#2b2117");

    /// <summary>The one-pixel label shadow colour, defaulting to #f3ead2.</summary>
    public Color LabelShadowColor { get; set; } = Color.FromHex("#f3ead2");

    /// <summary>The accepted icon or label under the pointer on the latest update, or null.</summary>
    public WorldMapHover? Hovered => _core.Hovered;

    /// <summary>Adds or updates a live marker at fixed screen size. Markers draw after icons and labels.</summary>
    /// <param name="id">The caller's stable marker identity.</param>
    /// <param name="mapPosition">The marker centre in east/north map metres.</param>
    /// <param name="heading">Clockwise rotation from north in radians, for north-pointing artwork.</param>
    /// <param name="sprite">The borrowed sprite, whose texture remains caller-owned.</param>
    public void SetMarker(string id, Vector2 mapPosition, float heading, WorldMapSprite sprite)
    {
        _core.SetMarker(id, mapPosition, heading, sprite.Size);
        _markerSprites[id] = sprite;
    }

    /// <summary>Removes a live marker, returning whether its identity was present.</summary>
    /// <param name="id">The caller's stable marker identity.</param>
    public bool RemoveMarker(string id)
    {
        bool removed = _core.RemoveMarker(id);
        _markerSprites.Remove(id);
        return removed;
    }

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

    /// <summary>Handles navigation, chunk uploads, screen-space overlay layout and pointer hover.</summary>
    /// <param name="pointer">This frame's pointer in the viewport's coordinate space.</param>
    /// <param name="wheelDelta">This frame's wheel delta.</param>
    public void Update(Pointer pointer, float wheelDelta) => _core.Update(pointer, wheelDelta);

    /// <summary>Draws chunks, labels, icons and markers without opening, closing or changing the batch's scissor.</summary>
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
        if (LabelFont is { } font)
        {
            foreach (ScreenLabel label in _core.OverlayPlacement.Labels)
            {
                Vector2 position = label.Position - label.Size / 2f;
                batch.DrawString(font, label.Text, position + Vector2.One, LabelShadowColor, label.Tier.TextScale);
                batch.DrawString(font, label.Text, position, LabelColor, label.Tier.TextScale);
            }
        }
        foreach (ScreenIcon icon in _core.OverlayPlacement.Icons)
        {
            if (IconSprites?.Invoke(icon.Key) is WorldMapSprite sprite)
                batch.Draw(sprite.Texture, icon.Position, icon.Size, new Vector2(0.5f), 0f, sprite.SourceUv, Color.White);
        }
        foreach (ScreenMarker marker in _core.Markers)
        {
            if (_markerSprites.TryGetValue(marker.Id, out WorldMapSprite sprite))
                batch.Draw(sprite.Texture, marker.Position, marker.Size, new Vector2(0.5f), marker.Heading, sprite.SourceUv, Color.White);
        }
    }

    /// <summary>Releases view state and surface-owned textures. Snapshot textures remain capture-owned.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.Dispose();
        _markerSprites.Clear();
        _primitives.Dispose();
        if (_ownsTextures) _white.Dispose();
    }
}
