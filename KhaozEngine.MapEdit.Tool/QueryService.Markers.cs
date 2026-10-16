using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapEdit;

public sealed partial class QueryService
{
    /// <summary>Effective markers of the open document in ordinal ID order. Each given filter must match: an exact
    /// ordinal name, role or tag, enabled markers only, and the half-open rectangle that includes its minima and
    /// excludes its maxima. A document with neither building intent nor prefabs has no markers and is not expanded.</summary>
    public IReadOnlyList<MapEffectiveMarker> MarkerList(string? name, string? role, string? tag, bool enabledOnly, MapMarkerRect? rect)
    {
        if (!session.WithDocument((doc, _) => HoldsMarkers(doc))) return Array.Empty<MapEffectiveMarker>();
        return session.WithNativeAssets(c =>
        {
            if (!HoldsMarkers(c.Document)) return Array.Empty<MapEffectiveMarker>();
            var resources = MapPrefabResolver.Expand(c.Document, c.Assets);
            var registry = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(resources));
            IEnumerable<MapEffectiveMarker> result = enabledOnly ? registry.Enabled : registry.All;
            if (name is not null)
                result = registry.FindByName(name) is { } named && (!enabledOnly || named.Enabled) ? [named] : [];
            if (role is not null) result = Keep(result, registry.ByRole(role, enabledOnly));
            if (tag is not null) result = Keep(result, registry.ByTag(tag, enabledOnly));
            if (rect is { } bounds)
            {
                var view = resources.CompleteView();
                result = Keep(result, registry.InRect(bounds, enabledOnly, m => MapMarkerSpaces.Query(view, m)).Select(p => p.Marker));
            }
            return (IReadOnlyList<MapEffectiveMarker>)Array.AsReadOnly(result.ToArray());
        });
    }

    static bool HoldsMarkers(MapDocument document) => document.BuildingIntent is not null || document.Prefabs is not null;

    static IEnumerable<MapEffectiveMarker> Keep(IEnumerable<MapEffectiveMarker> result, IEnumerable<MapEffectiveMarker> filter)
    {
        var ids = filter.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return result.Where(m => ids.Contains(m.Id));
    }
}
