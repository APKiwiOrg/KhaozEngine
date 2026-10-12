using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>Collects icon and label anchors through ordered style rules and half-open chunk ownership.</summary>
public static class WorldMapFeatureCollector
{
    /// <summary>Collects features whose centres or marker positions belong to the requested chunk.</summary>
    /// <param name="source">The authored surface to query over the chunk rectangle.</param>
    /// <param name="style">The chunk size and ordered icon and label rules.</param>
    /// <param name="chunk">The origin-aligned chunk address.</param>
    /// <returns>Anchors sorted by descending y, ascending x and ordinal icon key or marker name.</returns>
    public static WorldMapChunkFeatures Collect(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        MapRect area = WorldMapChunking.ChunkRect(chunk, style.ChunkMetres);
        var icons = new List<WorldMapIconAnchor>();
        var labels = new List<WorldMapLabelAnchor>();

        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(area, 0f, footprints);
        foreach (WorldMapFootprint footprint in footprints)
            if (WorldMapChunking.ChunkAt(footprint.Center, style.ChunkMetres, chunk.Level) == chunk)
                AddIcon(style.IconRules, WorldMapFeatureSource.Footprint, footprint.Tags, footprint.Center, icons);

        var markers = new List<WorldMapMarker>();
        source.CollectMarkers(area, markers);
        foreach (WorldMapMarker marker in markers)
        {
            if (WorldMapChunking.ChunkAt(marker.Position, style.ChunkMetres, chunk.Level) != chunk) continue;
            AddIcon(style.IconRules, WorldMapFeatureSource.Marker, marker.Tags, marker.Position, icons);
            if (style.Labels is { } rules) AddLabel(rules, marker, labels);
        }

        if (icons.Count == 0 && labels.Count == 0) return WorldMapChunkFeatures.Empty;
        icons.Sort((left, right) =>
        {
            int order = ComparePosition(left.Position, right.Position);
            return order != 0 ? order : StringComparer.Ordinal.Compare(left.IconKey, right.IconKey);
        });
        labels.Sort((left, right) =>
        {
            int order = ComparePosition(left.Position, right.Position);
            return order != 0 ? order : StringComparer.Ordinal.Compare(left.MarkerName, right.MarkerName);
        });
        return new WorldMapChunkFeatures(icons.AsReadOnly(), labels.AsReadOnly());
    }

    static void AddIcon(IReadOnlyList<WorldMapIconRule> rules, WorldMapFeatureSource kind,
        IReadOnlyList<string> tags, Vector2 position, List<WorldMapIconAnchor> icons)
    {
        foreach (WorldMapIconRule rule in rules)
        {
            if (rule.Source != kind || !WorldMapTags.HasAll(tags, rule.AllTags)) continue;
            icons.Add(new WorldMapIconAnchor(rule.IconKey, position, rule.Priority, rule.MergeRadiusPixels));
            break;
        }
    }

    static void AddLabel(WorldMapLabelRules rules, WorldMapMarker marker, List<WorldMapLabelAnchor> labels)
    {
        if (!WorldMapTags.HasAll(marker.Tags, [rules.LabelTag])) return;
        foreach (WorldMapLabelTier tier in rules.Tiers)
        {
            if (!WorldMapTags.HasAll(marker.Tags, [tier.Tag])) continue;
            labels.Add(new WorldMapLabelAnchor(marker.Name, marker.Position, tier));
            break;
        }
    }

    static int ComparePosition(Vector2 left, Vector2 right)
    {
        int order = right.Y.CompareTo(left.Y);
        return order != 0 ? order : left.X.CompareTo(right.X);
    }
}
