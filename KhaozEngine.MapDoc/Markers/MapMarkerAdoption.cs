using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Markers;

public enum MapLegacySpawnKind { Spawn, PlayerSpawn }
public sealed record MapLegacySpawnKey(MapLegacySpawnKind Kind, string Id);
public sealed record MapMarkerAdoptionChoice(string MarkerId, string Name, string Role, MapMarkerHeight Height, float? YawRadians = null);

/// <summary>Explicit, exhaustive adoption of both legacy spawn lists on a detached native document. Heights are
/// encoded as authored intent here. Bound command preparation validates their owners and resolves support.</summary>
public static class MapMarkerAdoption
{
    public static MapDocument ConvertSpawns(MapDocument document, IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(choices);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Cannot adopt spawns from a partial document.");
        MapMarkerEdits.RequireResolver(document);
        RequireValid(document);
        var sources = document.Spawns.Select(s => (Key: new MapLegacySpawnKey(MapLegacySpawnKind.Spawn, s.Id), s.X, s.Z,
            Yaw: 0f, s.Enabled, s.Tags, Archetype: (string?)s.ArchetypeId)).Concat(document.PlayerSpawns.Select(s =>
            (Key: new MapLegacySpawnKey(MapLegacySpawnKind.PlayerSpawn, s.Id), s.X, s.Z, s.Yaw, s.Enabled, s.Tags, Archetype: (string?)null))).ToArray();
        var keys = sources.Select(s => s.Key).ToHashSet();
        if (choices.Count != sources.Length || choices.Keys.Any(k => !keys.Contains(k)))
            throw new MapDocumentException("spawns_adopt requires exactly one choice for every Spawn and PlayerSpawn source, with no extras.");

        var ids = (document.BuildingIntent?.Markers ?? []).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var names = (document.BuildingIntent?.Markers ?? []).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        // Dormant bindings and retired transfers retain their typed marker IDs, as do live prefab children.
        if (document.Prefabs is { } prefabs)
            foreach (var binding in prefabs.Instances.SelectMany(i => i.Bindings.Entries).Concat(prefabs.Transfers.Select(t => t.Binding)))
                if (binding.Child.Kind == MapPrefabChildKind.Marker) ids.Add(binding.WorldId);
        var converted = new List<MapLocalMarkerDoc>(sources.Length);
        foreach (var source in sources)
        {
            if (!choices.TryGetValue(source.Key, out var choice) || choice is null || choice.Height is null)
                throw new MapDocumentException($"spawns_adopt requires a destination and height choice for {source.Key.Kind} '{source.Key.Id}'.");
            if (!ids.Add(choice.MarkerId)) throw new MapDocumentException($"Marker ID '{choice.MarkerId}' is live or reserved. Choose a distinct destination ID.");
            if (!names.Add(choice.Name)) throw new MapDocumentException($"Duplicate marker name '{choice.Name}'. Choose a distinct destination name.");
            converted.Add(MapMarkerHeights.Apply(new(1, choice.MarkerId, choice.Name, new(source.X, 0, source.Z),
                choice.YawRadians ?? source.Yaw, choice.Role, source.Enabled, source.Tags.ToArray()), choice.Height) with
            { ArchetypeId = source.Archetype });
        }

        // The released resolver-adoption copy pattern avoids monolithic patch embedding limits on complete tiled maps.
        var options = MapDocumentFile.CreateOptions(MapDocRegistry.CreateDefault(), write: false);
        var snapshot = MapTiledFile.GlobalsOnly(document);
        snapshot.Surfaces = new();
        snapshot.Placements = document.Placements;
        snapshot.Spawns = document.Spawns;
        snapshot.PlayerSpawns = document.PlayerSpawns;
        snapshot.TerrainOverrides = document.TerrainOverrides;
        var copy = JsonSerializer.Deserialize<MapDocument>(JsonSerializer.Serialize(snapshot, options), options)!;
        copy.Surfaces = document.Surfaces.Clone();
        copy.Tiles = document.Tiles;
        var intent = copy.BuildingIntent ?? new(1, [], [], [], []);
        copy.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights, intent.Markers.Concat(converted).ToArray())
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        copy.Spawns.Clear();
        copy.PlayerSpawns.Clear();
        RequireValid(copy);
        return copy;
    }

    static void RequireValid(MapDocument document)
    {
        var errors = MapDocumentValidator.Validate(document, MapDocRegistry.CreateDefault());
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
    }
}
