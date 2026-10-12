using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap;

/// <summary>Symbol kinds in draw order after the non-drawing <see cref="Hidden"/> kind.</summary>
public enum WorldMapSymbolKind
{
    /// <summary>No symbol is drawn.</summary>
    Hidden,
    /// <summary>A bridge over the terrain or water.</summary>
    Bridge,
    /// <summary>A blocking obstacle.</summary>
    Obstacle,
    /// <summary>A narrow wall.</summary>
    Wall,
    /// <summary>A building or roof footprint.</summary>
    Building,
    /// <summary>A tree canopy.</summary>
    Tree
}

/// <summary>The appearance of one footprint symbol.</summary>
/// <param name="Kind">The geometry and draw order.</param>
/// <param name="Fill">The fill colour.</param>
/// <param name="Outline">The outline colour.</param>
public sealed record WorldMapSymbol(WorldMapSymbolKind Kind, Color Fill, Color Outline)
{
    /// <summary>The non-drawing symbol with transparent colours.</summary>
    public static WorldMapSymbol Hidden { get; } = new(WorldMapSymbolKind.Hidden, Color.Transparent, Color.Transparent);
}

/// <summary>Optional all-of criteria for a footprint symbol rule. Null criteria are unrestricted.</summary>
/// <param name="ArchetypeKey">The required archetype key, or null for any archetype.</param>
/// <param name="AllTags">All required tags, or null for no tag restriction.</param>
/// <param name="IsRoof">The required roof flag, or null for either.</param>
/// <param name="Collision">The required collision role, or null for any role.</param>
public sealed record WorldMapFootprintMatch(
    string? ArchetypeKey = null, IReadOnlyList<string>? AllTags = null, bool? IsRoof = null, WorldMapCollision? Collision = null);

/// <summary>A footprint match and its symbol. The first matching style rule wins.</summary>
/// <param name="Match">The all-of footprint criteria.</param>
/// <param name="Symbol">The selected symbol.</param>
public sealed record WorldMapSymbolRule(WorldMapFootprintMatch Match, WorldMapSymbol Symbol);
