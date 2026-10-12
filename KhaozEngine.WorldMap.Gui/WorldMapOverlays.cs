using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>A borrowed sprite drawn at fixed screen size, with its artwork authored pointing north.</summary>
/// <param name="Texture">The caller-owned texture, which the map view never disposes.</param>
/// <param name="SourceUv">The source UV endpoints in (left, top, right, bottom) order.</param>
/// <param name="Size">The unrotated size in screen pixels.</param>
public readonly record struct WorldMapSprite(Texture2D Texture, Vector4 SourceUv, Vector2 Size);

/// <summary>The map overlay identified under the pointer.</summary>
public enum WorldMapHoverKind
{
    /// <summary>An authored feature icon.</summary>
    Icon,
    /// <summary>An authored place-name label.</summary>
    Label
}

/// <summary>An accepted icon or label under the pointer, for a caller-resolved tooltip.</summary>
/// <param name="Kind">The overlay kind.</param>
/// <param name="Key">The icon key or authored marker name.</param>
/// <param name="MapPosition">The original anchor in east/north map metres.</param>
public sealed record WorldMapHover(WorldMapHoverKind Kind, string Key, Vector2 MapPosition);

internal readonly record struct ScreenIcon(string Key, Vector2 Position, Vector2 Size, int Priority,
    float MergeRadius, Vector2 MapPosition);

internal readonly record struct ScreenLabel(string MarkerName, string Text, Vector2 Position, Vector2 Size,
    WorldMapLabelTier Tier, Vector2 MapPosition);

internal readonly record struct ScreenMarker(string Id, Vector2 Position, Vector2 Size, float Heading);

internal sealed record OverlayPlacement(IReadOnlyList<ScreenIcon> Icons, IReadOnlyList<ScreenLabel> Labels)
{
    internal WorldMapHover? Hover { get; set; }
}

/// <summary>Caller-owned scratch storage and a retained view of the latest accepted overlays.</summary>
internal sealed class WorldMapOverlayBuffers
{
    internal readonly List<(ScreenIcon Icon, int Index)> SortedIcons = [];
    internal readonly List<(ScreenLabel Label, int Index)> SortedLabels = [];
    internal readonly List<ScreenIcon> PlacedIcons = [];
    internal readonly List<ScreenLabel> PlacedLabels = [];
    internal readonly List<Rect> IconRects = [];
    internal readonly List<Rect> LabelRects = [];

    internal WorldMapOverlayBuffers() =>
        Placement = new OverlayPlacement(PlacedIcons.AsReadOnly(), PlacedLabels.AsReadOnly());

    internal OverlayPlacement Placement { get; }

    internal void Clear()
    {
        SortedIcons.Clear();
        SortedLabels.Clear();
        PlacedIcons.Clear();
        PlacedLabels.Clear();
        IconRects.Clear();
        LabelRects.Clear();
        Placement.Hover = null;
    }
}
