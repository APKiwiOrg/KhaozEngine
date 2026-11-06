using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The per-tile validation subset a tile read runs, keeping the format's loud-fail stance for a read
/// that cannot see the whole document.
/// <para><see cref="MapDocumentValidator.Validate(MapDocument, MapDocRegistry)"/> needs globals a tile file does not carry (bounds, layer
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
        var findings = new List<MapDocumentFinding>();
        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < content.Placements.Count; i++)
        {
            MapPlacement p = content.Placements[i];
            if (string.IsNullOrWhiteSpace(p.Id)) Add(Item(MapDocumentRefusal.SchemaValueRequired, "every placement needs a non-empty id.", "placements", i, "id", p.Id, "placement"));
            else if (!placementIds.Add(p.Id)) Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate placement id '{p.Id}' within the tile.", "placements", i, "id", p.Id, "placement"));
            if (string.IsNullOrWhiteSpace(p.Kind)) Add(Item(MapDocumentRefusal.SchemaValueRequired, $"placement '{p.Id}': kind must be non-empty.", "placements", i, "kind", p.Id, "placement"));
            if (p.Scale <= 0f) Add(Item(float.IsFinite(p.Scale) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                $"placement '{p.Id}': scale must be positive.", "placements", i, "scale", p.Id, "placement") with
                { ActualNumber = float.IsFinite(p.Scale) ? p.Scale : null, MinimumNumber = 0 });
            CheckInTile(p.X, p.Z, $"placement '{p.Id}'", "placements", i, p.Id, "placement");
        }
        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < content.Spawns.Count; i++)
        {
            MapSpawn s = content.Spawns[i];
            if (string.IsNullOrWhiteSpace(s.Id)) Add(Item(MapDocumentRefusal.SchemaValueRequired, "every spawn needs a non-empty id.", "spawns", i, "id", s.Id, "spawn"));
            else if (!spawnIds.Add(s.Id)) Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate spawn id '{s.Id}' within the tile.", "spawns", i, "id", s.Id, "spawn"));
            if (string.IsNullOrWhiteSpace(s.ArchetypeId)) Add(Item(MapDocumentRefusal.SchemaValueRequired, $"spawn '{s.Id}': archetypeId must be non-empty.", "spawns", i, "archetypeId", s.Id, "spawn"));
            CheckInTile(s.X, s.Z, $"spawn '{s.Id}'", "spawns", i, s.Id, "spawn");
        }
        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < content.PlayerSpawns.Count; i++)
        {
            MapPlayerSpawn s = content.PlayerSpawns[i];
            if (string.IsNullOrWhiteSpace(s.Id)) Add(Item(MapDocumentRefusal.SchemaValueRequired, "every player spawn needs a non-empty id.", "playerSpawns", i, "id", s.Id, "player spawn"));
            else if (!playerSpawnIds.Add(s.Id)) Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate player spawn id '{s.Id}' within the tile.", "playerSpawns", i, "id", s.Id, "player spawn"));
            CheckInTile(s.X, s.Z, $"player spawn '{s.Id}'", "playerSpawns", i, s.Id, "player spawn");
        }
        var sculptCoords = new HashSet<(int, int)>();
        for (int i = 0; i < content.SculptTiles.Count; i++)
        {
            MapSculptTile t = content.SculptTiles[i];
            if (!sculptCoords.Add((t.TileX, t.TileZ)))
                Add(new(MapDocumentRefusal.IdentityDuplicate, $"duplicate sculpt tile ({t.TileX}, {t.TileZ}) within the tile.")
                { SculptTile = new(t.TileX, t.TileZ), SubjectKind = "sculpt tile", Index = i, FieldPath = $"$.sculpt[{i}]" });
            float span = TerrainSculpt.TileSize * sculptCellSize;
            float x = t.TileX * span, z = t.TileZ * span;
            MapTileCoord owner = MapTileGrid.CoordOf(x, z, tileSize);
            if (owner != content.Coord)
                Add(new(MapDocumentRefusal.DocumentTileOwnershipMismatch,
                    $"sculpt tile ({t.TileX}, {t.TileZ}) is owned by document tile ({owner.X}, {owner.Z}), not this one.")
                { SculptTile = new(t.TileX, t.TileZ), SubjectKind = "sculpt tile", Index = i, FieldPath = $"$.sculpt[{i}]",
                    PositionX = x, PositionZ = z });
        }
        if (findings.Count > 0)
            throw MapDocumentRefusalFactory.FromFinding(findings[0],
                $"{where}: tile ({content.Coord.X}, {content.Coord.Z}) in '{file}' is invalid:\n  " + string.Join("\n  ", findings.Select(f => f.Message)), findings: findings);

        void Add(MapDocumentFinding finding) => findings.Add(finding with { Tile = content.Coord, SourceName = where, Path = file });
        void CheckInTile(float x, float z, string what, string collection, int index, string id, string kind)
        {
            MapTileCoord actual = MapTileGrid.CoordOf(x, z, tileSize);
            if (actual != content.Coord)
                Add(new(MapDocumentRefusal.DocumentTileOwnershipMismatch,
                    $"{what} at ({x}, {z}) belongs to document tile ({actual.X}, {actual.Z}), not this one.")
                { FieldPath = $"$.{collection}[{index}]", EntityId = id, SubjectKind = kind, Index = index, PositionX = x, PositionZ = z,
                    PlacementId = kind == "placement" ? id : null });
        }
    }

    static MapDocumentFinding Item(MapDocumentRefusal reason, string message, string collection, int index,
        string field, string id, string kind) => new(reason, message)
        { FieldPath = $"$.{collection}[{index}].{field}", EntityId = id, SubjectKind = kind, Index = index,
            PlacementId = kind == "placement" ? id : null };

    /// <summary>The world span of one sculpt tile, in meters. A document tile must be at least this wide, or
    /// the origin-corner ownership rule would assign sculpt tiles to document tiles that do not cover
    /// them.</summary>
    internal static float SculptSpan(float sculptCellSize) => TerrainSculpt.TileSize * sculptCellSize;
}
