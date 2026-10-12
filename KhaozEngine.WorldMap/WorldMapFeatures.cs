using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>An icon anchored to an authored feature in map space.</summary>
/// <param name="IconKey">The game's icon key, resolved outside the map core.</param>
/// <param name="Position">The anchor position in map metres.</param>
/// <param name="Priority">The priority used when screen-space icons overlap.</param>
/// <param name="MergeRadiusPixels">The screen-space radius for merging icons with the same key.</param>
public sealed record WorldMapIconAnchor(string IconKey, Vector2 Position, int Priority, float MergeRadiusPixels);

/// <summary>A place-name label anchored to an authored marker in map space.</summary>
/// <param name="MarkerName">The authored marker name from which the game resolves localized text.</param>
/// <param name="Position">The anchor position in map metres.</param>
/// <param name="Tier">The label's selected zoom, rank and text-scale settings.</param>
public sealed record WorldMapLabelAnchor(string MarkerName, Vector2 Position, WorldMapLabelTier Tier);

/// <summary>The icon and label anchors owned by a single map chunk.</summary>
/// <param name="Icons">Icons ordered north to south, then west to east, then by ordinal icon key.</param>
/// <param name="Labels">Labels ordered north to south, then west to east, then by ordinal marker name.</param>
public sealed record WorldMapChunkFeatures(IReadOnlyList<WorldMapIconAnchor> Icons, IReadOnlyList<WorldMapLabelAnchor> Labels)
{
    /// <summary>A shared result containing no icon or label anchors.</summary>
    public static WorldMapChunkFeatures Empty { get; } = new(
        Array.Empty<WorldMapIconAnchor>(), Array.Empty<WorldMapLabelAnchor>());
}
