using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.WorldMap;

/// <summary>Caches the source filters needed by each immutable style instance.</summary>
internal sealed class WorldMapFeatureQuery
{
    static readonly ConditionalWeakTable<WorldMapStyle, Lazy<WorldMapFeatureQuery>> Queries = new();

    WorldMapFeatureQuery(WorldMapStyle style)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        bool allFootprints = false;
        NeedsMarkers = style.Labels is not null;
        foreach (WorldMapIconRule rule in style.IconRules)
        {
            if (rule.Source == WorldMapFeatureSource.Marker)
            {
                NeedsMarkers = true;
                continue;
            }
            if (rule.Source != WorldMapFeatureSource.Footprint) continue;
            NeedsFootprints = true;
            if (rule.AllTags.Count == 0) allFootprints = true;
            foreach (string tag in rule.AllTags) tags.Add(tag);
        }
        AnyTags = allFootprints ? null : Array.AsReadOnly(new List<string>(tags).ToArray());
    }

    internal bool NeedsFootprints { get; }
    internal bool NeedsMarkers { get; }
    internal IReadOnlyCollection<string>? AnyTags { get; }

    internal static WorldMapFeatureQuery ForStyle(WorldMapStyle style) =>
        Queries.GetValue(style, static key => new Lazy<WorldMapFeatureQuery>(() => new(key))).Value;
}
