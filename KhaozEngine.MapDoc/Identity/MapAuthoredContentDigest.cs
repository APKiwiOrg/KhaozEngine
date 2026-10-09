using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>Storage-independent digest of the non-global authored content: placements, spawns, player spawns and
/// sculpt tiles. Each entry is normalized as scheme 1 normalizes it (the field representation the whole writer
/// emits, members in ordinal order, placement <c>displayName</c> stripped) and hashed on its own. The digest is
/// the ordered stream of (kind, id or sculpt tile, entry digest), so a caller holds ids and digests, never whole
/// tiles. Lists are ordered by ordinal id and sculpt by sculpt tile coordinate, so monolithic, tiled and repacked
/// copies of one document give one digest. An empty sculpt block equals an absent one. Its cell size is a global
/// that the root digest covers.</summary>
internal sealed class MapAuthoredContentDigest
{
    const string Domain = "kemap/native-content/1";
    const string PlacementKind = "placement", SpawnKind = "spawn", PlayerSpawnKind = "playerSpawn", SculptKind = "sculpt";

    // Field representation SaveText writes. Indentation does not reach a node.
    readonly JsonSerializerOptions _options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
    readonly SortedDictionary<string, string> _placements = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, string> _spawns = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, string> _playerSpawns = new(StringComparer.Ordinal);
    readonly SortedDictionary<(int TileZ, int TileX), string> _sculpt = new();

    /// <summary>The digest of a complete in-memory document. Duplicate ids or sculpt tiles refuse.</summary>
    internal static string Compute(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var digest = new MapAuthoredContentDigest();
        string? duplicate = digest.Add(document.Placements, document.Spawns, document.PlayerSpawns,
            document.TerrainOverrides?.Tiles ?? Array.Empty<MapSculptTile>());
        if (duplicate is not null)
            throw new MapDocumentException($"Whole authored identity found duplicate authored content {duplicate}.");
        return digest.Finish();
    }

    /// <summary>Adds entries and returns null, or names the first entry already present.</summary>
    internal string? Add(IEnumerable<MapPlacement> placements, IEnumerable<MapSpawn> spawns,
        IEnumerable<MapPlayerSpawn> playerSpawns, IEnumerable<MapSculptTile> sculpt)
    {
        foreach (MapPlacement placement in placements)
        {
            JsonObject node = JsonSerializer.SerializeToNode(placement, _options)!.AsObject();
            node.Remove("displayName");
            if (!_placements.TryAdd(placement.Id, EntryDigest(PlacementKind, node))) return $"placement '{placement.Id}'";
        }
        foreach (MapSpawn spawn in spawns)
            if (!_spawns.TryAdd(spawn.Id, EntryDigest(SpawnKind, JsonSerializer.SerializeToNode(spawn, _options))))
                return $"spawn '{spawn.Id}'";
        foreach (MapPlayerSpawn spawn in playerSpawns)
            if (!_playerSpawns.TryAdd(spawn.Id, EntryDigest(PlayerSpawnKind, JsonSerializer.SerializeToNode(spawn, _options))))
                return $"player spawn '{spawn.Id}'";
        foreach (MapSculptTile tile in sculpt)
            if (!_sculpt.TryAdd((tile.TileZ, tile.TileX), SculptDigest(tile)))
                return $"sculpt tile ({tile.TileX}, {tile.TileZ})";
        return null;
    }

    internal string Finish() => MapCanonical.HashHex(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", Domain);
        w.WriteStartArray("entries");
        WriteKind(w, PlacementKind, _placements);
        WriteKind(w, SpawnKind, _spawns);
        WriteKind(w, PlayerSpawnKind, _playerSpawns);
        foreach (var tile in _sculpt)
        {
            w.WriteStartArray();
            w.WriteStringValue(SculptKind);
            w.WriteNumberValue(tile.Key.TileX);
            w.WriteNumberValue(tile.Key.TileZ);
            w.WriteStringValue(tile.Value);
            w.WriteEndArray();
            w.Flush();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    static void WriteKind(Utf8JsonWriter w, string kind, SortedDictionary<string, string> entries)
    {
        foreach (var entry in entries)
        {
            w.WriteStartArray();
            w.WriteStringValue(kind);
            w.WriteStringValue(entry.Key);
            w.WriteStringValue(entry.Value);
            w.WriteEndArray();
            w.Flush();
        }
    }

    static string EntryDigest(string kind, JsonNode? value) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue(Domain);
        w.WriteStringValue(kind);
        MapSurfaceRootProjection.WriteNormalized(w, value);
        w.WriteEndArray();
    });

    // The { tileX, tileZ, deltas } shape the whole writer emits, members in ordinal order.
    static string SculptDigest(MapSculptTile tile) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue(Domain);
        w.WriteStringValue(SculptKind);
        w.WriteStartObject();
        w.WriteStartArray("deltas");
        foreach (float delta in tile.Deltas) w.WriteNumberValue(delta);
        w.WriteEndArray();
        w.WriteNumber("tileX", tile.TileX);
        w.WriteNumber("tileZ", tile.TileZ);
        w.WriteEndObject();
        w.WriteEndArray();
    });
}
