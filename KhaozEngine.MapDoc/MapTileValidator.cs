using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The per-tile validation subset a tile read runs, keeping the format's loud-fail stance for a read
/// that cannot see the whole document.
/// <para><see cref="MapDocumentValidator.Validate"/> needs globals a tile file does not carry (bounds, layer
/// names), so a tile read cannot use it. What IS checkable from a tile plus the manifest's grid headers is
/// checked here, and bounds and cross-tile reference checks stay with the whole-document validator, with
/// <see cref="MapDocumentFile.VerifyTiled"/> as the whole-world check.</para>
/// <para>Id uniqueness is scoped to the tile: one tile cannot see another, so global uniqueness is a
/// whole-load and <c>VerifyTiled</c> concern. The last check is the one a whole-document load can never make
/// and a tiled load must: every item actually falls inside the tile it was read from. That is what catches a
/// hand-edited or tool-generated file whose content does not match its name.</para>
/// <para>The spec also lists "every kind or asset reference resolves against the
/// <see cref="MapDocRegistry"/>". Nothing in a tile file is registry-resolvable: the registry maps terrain
/// FEATURE discriminators, and features live in the manifest, so that clause has no target here. Placement
/// kinds are checked the way the whole-document validator checks them, for non-emptiness.</para></summary>
internal static class MapTileValidator
{
    internal static void Validate(MapTileContent content, string where, string file, float tileSize, float sculptCellSize)
    {
        var errors = new List<string>();

        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in content.Placements)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) errors.Add("every placement needs a non-empty id.");
            else if (!placementIds.Add(p.Id)) errors.Add($"duplicate placement id '{p.Id}' within the tile.");
            if (string.IsNullOrWhiteSpace(p.Kind)) errors.Add($"placement '{p.Id}': kind must be non-empty.");
            if (p.Scale <= 0f) errors.Add($"placement '{p.Id}': scale must be positive.");
            CheckInTile(p.X, p.Z, content.Coord, tileSize, $"placement '{p.Id}'", errors);
        }

        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSpawn s in content.Spawns)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every spawn needs a non-empty id.");
            else if (!spawnIds.Add(s.Id)) errors.Add($"duplicate spawn id '{s.Id}' within the tile.");
            if (string.IsNullOrWhiteSpace(s.ArchetypeId)) errors.Add($"spawn '{s.Id}': archetypeId must be non-empty.");
            CheckInTile(s.X, s.Z, content.Coord, tileSize, $"spawn '{s.Id}'", errors);
        }

        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlayerSpawn s in content.PlayerSpawns)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every player spawn needs a non-empty id.");
            else if (!playerSpawnIds.Add(s.Id)) errors.Add($"duplicate player spawn id '{s.Id}' within the tile.");
            CheckInTile(s.X, s.Z, content.Coord, tileSize, $"player spawn '{s.Id}'", errors);
        }

        var sculptCoords = new HashSet<(int, int)>();
        foreach (MapSculptTile t in content.SculptTiles)
        {
            if (!sculptCoords.Add((t.TileX, t.TileZ)))
                errors.Add($"duplicate sculpt tile ({t.TileX}, {t.TileZ}) within the tile.");
            MapTileCoord owner = MapTileGrid.OwnerOfSculptTile(t.TileX, t.TileZ, sculptCellSize, tileSize);
            if (owner != content.Coord)
                errors.Add($"sculpt tile ({t.TileX}, {t.TileZ}) is owned by document tile ({owner.X}, {owner.Z}), not this one.");
        }

        if (errors.Count > 0)
            throw new MapDocumentException(
                $"{where}: tile ({content.Coord.X}, {content.Coord.Z}) in '{file}' is invalid:\n  " + string.Join("\n  ", errors));
    }

    static void CheckInTile(float x, float z, MapTileCoord coord, float tileSize, string what, List<string> errors)
    {
        MapTileCoord actual = MapTileGrid.CoordOf(x, z, tileSize);
        if (actual != coord)
            errors.Add($"{what} at ({x}, {z}) belongs to document tile ({actual.X}, {actual.Z}), not this one.");
    }

    /// <summary>The world span of one sculpt tile, in meters. A document tile must be at least this wide, or
    /// the origin-corner ownership rule would assign sculpt tiles to document tiles that do not cover
    /// them.</summary>
    internal static float SculptSpan(float sculptCellSize) => TerrainSculpt.TileSize * sculptCellSize;
}
