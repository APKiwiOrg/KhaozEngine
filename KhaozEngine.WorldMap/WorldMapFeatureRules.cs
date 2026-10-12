using System.Collections.Generic;

namespace KhaozEngine.WorldMap;

/// <summary>The authored data from which an icon anchor is derived.</summary>
public enum WorldMapFeatureSource
{
    /// <summary>A placed object's surface footprint.</summary>
    Footprint,
    /// <summary>A named surface marker.</summary>
    Marker
}

/// <summary>A tag match selecting an icon and its screen-space merge policy.</summary>
/// <param name="Source">The kind of authored data to match.</param>
/// <param name="AllTags">Every tag required for a match.</param>
/// <param name="IconKey">The game's icon key, resolved outside the map core.</param>
/// <param name="Priority">The priority used to select an icon when anchors merge.</param>
/// <param name="MergeRadiusPixels">The screen-space merge radius in pixels.</param>
public sealed record WorldMapIconRule(
    WorldMapFeatureSource Source, IReadOnlyList<string> AllTags, string IconKey, int Priority, float MergeRadiusPixels = 24f);

/// <summary>A tagged label tier with a zoom range and text scale.</summary>
/// <param name="Tag">The marker tag identifying this tier.</param>
/// <param name="Rank">The label's ordering rank.</param>
/// <param name="MinPixelsPerMetre">The minimum map scale at which the tier is visible.</param>
/// <param name="MaxPixelsPerMetre">The maximum map scale at which the tier is visible.</param>
/// <param name="TextScale">The scale applied to the game's label text.</param>
public sealed record WorldMapLabelTier(string Tag, int Rank, float MinPixelsPerMetre, float MaxPixelsPerMetre, float TextScale);

/// <summary>Identifies label markers and their zoom-dependent tiers.</summary>
/// <param name="LabelTag">The marker tag that selects a label.</param>
/// <param name="Tiers">The ordered tagged tiers.</param>
public sealed record WorldMapLabelRules(string LabelTag, IReadOnlyList<WorldMapLabelTier> Tiers);
