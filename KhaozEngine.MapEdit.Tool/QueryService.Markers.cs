using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEdit;

public sealed partial class QueryService
{
    /// <summary>Effective markers of the open document in ordinal ID order. Each given filter must match: an exact
    /// ordinal name, role or tag, enabled markers only, and the half-open rectangle that includes its minima and
    /// excludes its maxima. A document with neither building intent nor prefabs has no markers and is not expanded.</summary>
    public IReadOnlyList<MapEffectiveMarker> MarkerList(string? name, string? role, string? tag, bool enabledOnly, MapMarkerRect? rect) =>
        MarkerList(name, role, tag, enabledOnly, rect, out _);

    /// <summary>As the public list, also reporting whether a complete space view was built. The view is built only when
    /// a rectangle candidate needs membership, after the registry's broad phase and exact test.</summary>
    internal IReadOnlyList<MapEffectiveMarker> MarkerList(string? name, string? role, string? tag, bool enabledOnly, MapMarkerRect? rect,
        out bool builtCompleteView)
    {
        builtCompleteView = false;
        if (!session.WithDocument((doc, _) => HoldsMarkers(doc))) return Array.Empty<MapEffectiveMarker>();
        bool built = false;
        IReadOnlyList<MapEffectiveMarker> list = session.WithNativeAssets(c =>
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
                MapScopedSurfaces? view = null;
                MapScopedSurfaces View()
                {
                    built = true;
                    return view ??= resources.CompleteView();
                }
                result = Keep(result, registry.InRect(bounds, enabledOnly, m => MapMarkerSpaces.Query(View(), m)).Select(p => p.Marker));
            }
            return (IReadOnlyList<MapEffectiveMarker>)Array.AsReadOnly(result.ToArray());
        });
        builtCompleteView = built;
        return list;
    }

    // Resolver (1, 1) documents keep the analytic support recipe their identity names. The shared resolver classifier
    // decides, never a second pattern.
    static bool UsesAuthoredSupport(MapDocument document) =>
        NativeDocumentService.IsNative(document) && MapNativeResolution.ResolverVersionOf(document.ResolverIdentity) == 2;

    /// <summary>R6-P7. The inclusive rectangle over a resolver-2 native document. Placements are the effective resolved
    /// transforms of the actual native resolution, prefab placements included, and a support-bound placement reports
    /// its seated height. Old native spawns sample the caller's <see cref="MapMarkerConsumerOptions.LegacySpawnSurfaceId"/>
    /// at their XZ. Only Supported succeeds: any other sample refuses the whole query, naming each spawn, its typed status
    /// and the Surface owner, so no entry ever carries an invented height. Without that Surface owner, a rectangle holding
    /// an old spawn refuses before any resolution. Adopted marker starts are read through the marker summary and views,
    /// never as old lists.</summary>
    PlacementsInRectResult NativePlacementsInRect(float minX, float minZ, float maxX, float maxZ) => session.WithNativeAssets(c =>
    {
        const string Guidance = "Adopt them as markers with spawns_adopt and author heights with marker_set";
        bool Inside(float x, float z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;
        MapSpawn[] spawns = c.Document.Spawns.Where(s => Inside(s.X, s.Z)).ToArray();
        MapPlayerSpawn[] players = c.Document.PlayerSpawns.Where(s => Inside(s.X, s.Z)).ToArray();
        string? surfaceId = session.MarkerOptions.LegacySpawnSurfaceId;
        if (surfaceId is null && (spawns.Length != 0 || players.Length != 0))
            throw new MapDocumentException(
                "placements_in_rect: this native document holds unadopted legacy spawns in the rectangle, and their height comes " +
                "only from a caller-supplied Surface owner. Reopen with markerOptionsJson legacySpawnSurfaceId, or adopt them as " +
                "markers with spawns_adopt and author heights with marker_set.");
        MapResolvedDocument resolved = MapNativeResolution.Resolve(c.Document, c.Assets, NativeDocumentService.SessionOptionsV2);
        MapEffectiveResources resources = resolved.EffectiveResources
            ?? throw new MapDocumentException("placements_in_rect: native resolution published no effective resources.");
        var seated = resources.PlacementSupports.Select(s => s.PlacementId).ToHashSet(StringComparer.Ordinal);
        PlacementEntry[] placements = resolved.Placements
            .Where(p => Inside(p.Transform.Position.X, p.Transform.Position.Z))
            .Select(p => new PlacementEntry(p.PlacementId, p.Kind, p.Transform.Position.X, p.Transform.Position.Y, p.Transform.Position.Z,
                p.Transform.YawRadians, p.Transform.Scale, !seated.Contains(p.PlacementId), p.Tags.ToArray()))
            .ToArray();
        MapSupportResult Sample(float x, float z) =>
            MapNativePointReadout.SurfaceAt(resources, new MapExactXz(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z)), surfaceId!,
                NativeDocumentService.SessionOptionsV2.QueryLimits);
        var npcSupports = spawns.Select(s => Sample(s.X, s.Z)).ToArray();
        var startSupports = players.Select(s => Sample(s.X, s.Z)).ToArray();
        string[] refused = spawns.Select((s, i) => (Kind: "Spawn", s.Id, Support: npcSupports[i]))
            .Concat(players.Select((s, i) => (Kind: "PlayerSpawn", s.Id, Support: startSupports[i])))
            .Where(r => r.Support.Status != MapSupportStatus.Supported)
            .Select(r => $"legacy {r.Kind} '{r.Id}' support status {r.Support.Status} on Surface '{surfaceId}': {r.Support.Detail}")
            .ToArray();
        if (refused.Length != 0)
            throw new MapDocumentException(
                $"placements_in_rect: legacySpawnSurfaceId '{surfaceId}' does not support every legacy spawn in the rectangle. " +
                string.Join(" ", refused.Select(r => r + ".")) + " " + Guidance + ", or reopen with a Surface owner that supports them.");
        SpawnEntry[] npcs = spawns.Select((s, i) => new SpawnEntry(s.Id, s.ArchetypeId, s.X, npcSupports[i].WorldY, s.Z, s.Enabled,
            s.Tags.ToArray())
        { Support = npcSupports[i] }).ToArray();
        PlayerSpawnEntry[] starts = players.Select((s, i) => new PlayerSpawnEntry(s.Id, s.X, startSupports[i].WorldY, s.Z, s.Yaw, s.Enabled,
            s.Tags.ToArray())
        { Support = startSupports[i] }).ToArray();
        return new PlacementsInRectResult(placements, npcs, starts);
    });

    static bool HoldsMarkers(MapDocument document) => document.BuildingIntent is not null || document.Prefabs is not null;

    static IEnumerable<MapEffectiveMarker> Keep(IEnumerable<MapEffectiveMarker> result, IEnumerable<MapEffectiveMarker> filter)
    {
        var ids = filter.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return result.Where(m => ids.Contains(m.Id));
    }
}
