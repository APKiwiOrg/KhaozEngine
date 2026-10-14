using System;

namespace KhaozEngine.WorldMap;

/// <summary>Selects footprint symbols through ordered, all-of style rules.</summary>
public static class WorldMapSymbolResolver
{
    /// <summary>Tests every non-null criterion, using ordinal archetype and all-of tag comparison.</summary>
    /// <param name="match">The criteria whose null fields are unrestricted.</param>
    /// <param name="footprint">The authored footprint to test.</param>
    /// <returns>Whether every specified criterion matches.</returns>
    public static bool Matches(WorldMapFootprintMatch match, WorldMapFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(footprint);
        return (match.ArchetypeKey is null || string.Equals(match.ArchetypeKey, footprint.ArchetypeKey, StringComparison.Ordinal)) &&
            (match.AllTags is null || WorldMapTags.HasAll(footprint.Tags, match.AllTags)) &&
            (match.IsRoof is null || match.IsRoof == footprint.IsRoof) &&
            (match.Collision is null || match.Collision == footprint.Collision);
    }

    /// <summary>Returns the first matching symbol, falling back only for blocking footprints.</summary>
    /// <param name="style">The ordered symbol rules and blocking fallback.</param>
    /// <param name="footprint">The authored footprint to resolve.</param>
    /// <returns>The first matching symbol, the blocking fallback, or the hidden symbol.</returns>
    public static WorldMapSymbol SymbolFor(WorldMapStyle style, WorldMapFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(footprint);
        foreach (WorldMapSymbolRule rule in style.SymbolRules)
            if (Matches(rule.Match, footprint)) return rule.Symbol;
        return footprint.Blocking ? style.FallbackBlocking : WorldMapSymbol.Hidden;
    }
}
