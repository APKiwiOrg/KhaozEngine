using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>A player start. The marker keeps its exact XYZ, yaw, ordered tags, enabled state, name, ID and height
/// provenance.</summary>
public sealed record MapPlayerStartView(MapEffectiveMarker Marker);

/// <summary>A spawn point. A payload-2 archetype is carried as given. A tag-driven spawn has a null archetype, and the
/// caller reads its tags from the marker.</summary>
public sealed record MapSpawnView(MapEffectiveMarker Marker, string? ArchetypeId);

/// <summary>A landmark is a point. It adds no region, disc or geometry.</summary>
public sealed record MapLandmarkView(MapEffectiveMarker Marker);

/// <summary>Typed, lossless views over a registry for one caller-supplied role, in ordinal ID order. The engine defines
/// no role. With enabledOnly false a view lists disabled markers too, for validation and persistence keys. With
/// enabledOnly true it lists only what a consumer should spawn.</summary>
public static class MapMarkerProjection
{
    public static IReadOnlyList<MapPlayerStartView> PlayerStarts(MapMarkerRegistry registry, string role, bool enabledOnly) =>
        Project(registry, role, enabledOnly, m => new MapPlayerStartView(m));

    public static IReadOnlyList<MapSpawnView> Spawns(MapMarkerRegistry registry, string role, bool enabledOnly) =>
        Project(registry, role, enabledOnly, m => new MapSpawnView(m, m.ArchetypeId));

    public static IReadOnlyList<MapLandmarkView> Landmarks(MapMarkerRegistry registry, string role, bool enabledOnly) =>
        Project(registry, role, enabledOnly, m => new MapLandmarkView(m));

    static IReadOnlyList<T> Project<T>(MapMarkerRegistry registry, string role, bool enabledOnly, Func<MapEffectiveMarker, T> view)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return Array.AsReadOnly(registry.ByRole(role, enabledOnly).Select(view).ToArray());
    }
}
