using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Semantic validation beyond what deserialization enforces. Returns human-readable errors (empty =
/// valid). <see cref="MapDocumentFile"/> runs this on every load and save and throws on any error, per the
/// loud-fail stance for dev-authored content.</summary>
public static class MapDocumentValidator
{
    public static IReadOnlyList<string> Validate(MapDocument doc, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(registry);
        var errors = new List<string>();

        if (doc.FormatVersion != MapDocumentFile.CurrentFormatVersion)
            errors.Add($"formatVersion is {doc.FormatVersion}, expected {MapDocumentFile.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(doc.Id))
            errors.Add("id must be non-empty.");
        if (!(doc.Bounds.MaxX > doc.Bounds.MinX) || !(doc.Bounds.MaxZ > doc.Bounds.MinZ))
            errors.Add("bounds must satisfy MaxX > MinX and MaxZ > MinZ.");
        MapNativeValidation.Validate(doc, errors);
        ValidateTileSize(doc, errors);
        if (doc.TerrainOverrides is { } overrides)
            ValidateOverrides(overrides, doc.Bounds, errors);

        var layerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapScatterLayer layer in doc.ScatterLayers)
        {
            if (string.IsNullOrWhiteSpace(layer.Name)) { errors.Add("every scatter layer needs a non-empty name."); continue; }
            if (!layerNames.Add(layer.Name)) errors.Add($"duplicate scatter layer name '{layer.Name}'.");
            if (layer.CellSize <= 0f) errors.Add($"scatter layer '{layer.Name}': cellSize must be positive.");
            if (layer.ScaleMax < layer.ScaleMin) errors.Add($"scatter layer '{layer.Name}': scaleMax must be >= scaleMin.");
        }

        var companionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapCompanionLayer layer in doc.CompanionLayers)
        {
            if (string.IsNullOrWhiteSpace(layer.Name)) { errors.Add("every companion layer needs a non-empty name."); continue; }
            if (!companionNames.Add(layer.Name)) errors.Add($"duplicate companion layer name '{layer.Name}'.");
            if (!layerNames.Contains(layer.HostLayer))
                errors.Add($"companion layer '{layer.Name}': host layer '{layer.HostLayer}' is not a scatter layer in this document.");
            if (layer.CountMax < layer.CountMin) errors.Add($"companion layer '{layer.Name}': countMax must be >= countMin.");
        }

        var exclusionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            MapExclusion e = doc.Exclusions[i];
            if (e.Shape is null) errors.Add($"exclusions[{i}]: shape is required.");
            if (!string.IsNullOrEmpty(e.Name) && !exclusionNames.Add(e.Name))
                errors.Add($"duplicate exclusion name '{e.Name}'.");
            CheckLayerRefs(e.Layers, layerNames, $"exclusions[{i}]", errors);
        }

        var scatterOverrideNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            MapScatterOverrideDoc o = doc.ScatterOverrides[i];
            if (o.Shape is null) errors.Add($"scatterOverrides[{i}]: shape is required.");
            if (o.DensityMultiplier < 0f) errors.Add($"scatterOverrides[{i}]: densityMultiplier must be >= 0.");
            if (!string.IsNullOrEmpty(o.Name) && !scatterOverrideNames.Add(o.Name))
                errors.Add($"duplicate scatter override name '{o.Name}'.");
            CheckLayerRefs(o.Layers, layerNames, $"scatterOverrides[{i}]", errors);
        }

        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in doc.Placements) ValidatePlacement(p, placementIds, errors);

        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSpawn s in doc.Spawns) ValidateSpawn(s, spawnIds, errors);

        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlayerSpawn s in doc.PlayerSpawns) ValidatePlayerSpawn(s, playerSpawnIds, errors);

        var regionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapRegion r in doc.Regions)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) errors.Add("every region needs a non-empty name.");
            else if (!regionNames.Add(r.Name)) errors.Add($"duplicate region name '{r.Name}'.");
            if (r.Shape is null) errors.Add($"region '{r.Name}': shape is required.");
        }

        var featureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapFeature f in doc.Terrain.Features)
        {
            if (!registry.TryGetFeatureDocType(f.Type, out _))
                errors.Add($"terrain feature type '{f.Type}' is not registered on the MapDocRegistry.");
            if (!string.IsNullOrEmpty(f.Name) && !featureNames.Add(f.Name))
                errors.Add($"duplicate terrain feature name '{f.Name}'.");
        }

        return errors;
    }

    /// <summary>The per-entity rules of <see cref="Validate"/> for one slice of a document's content, such as one
    /// stored tile, in the order <see cref="Validate"/> reports them. <paramref name="globals"/> carries the
    /// document's globals, already validated, and <paramref name="surfaces"/> its declared surfaces.
    /// <paramref name="numericIds"/> accumulates numeric IDs across slices. Duplicate string IDs across slices stay
    /// with the caller.</summary>
    internal static List<string> ValidateContent(MapDocument globals, IReadOnlyDictionary<string, MapSurfaceRef> surfaces,
        MapTileContent content, HashSet<long> numericIds)
    {
        var errors = new List<string>();
        foreach (MapPlacement p in content.Placements)
            MapNativeValidation.ValidateNumericId(p, globals.NumericIdHighWaterMark, numericIds, errors);
        foreach (MapPlacement p in content.Placements) MapNativeValidation.ValidateSupportBinding(p, surfaces, errors);
        if (globals.TerrainOverrides is { } overrides)
            foreach (MapSculptTile tile in content.SculptTiles) ValidateSculptTile(tile, overrides.CellSize, globals.Bounds, errors);
        foreach (MapPlacement p in content.Placements) ValidatePlacement(p, null, errors);
        foreach (MapSpawn s in content.Spawns) ValidateSpawn(s, null, errors);
        foreach (MapPlayerSpawn s in content.PlayerSpawns) ValidatePlayerSpawn(s, null, errors);
        return errors;
    }

    // A null ID set skips the duplicate check, for a caller that checks uniqueness another way.
    static void ValidatePlacement(MapPlacement p, HashSet<string>? ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(p.Id)) errors.Add("every placement needs a non-empty id.");
        else if (ids is not null && !ids.Add(p.Id)) errors.Add($"duplicate placement id '{p.Id}'.");
        if (string.IsNullOrWhiteSpace(p.Kind)) errors.Add($"placement '{p.Id}': kind must be non-empty.");
        if (p.Scale <= 0f) errors.Add($"placement '{p.Id}': scale must be positive.");
    }

    static void ValidateSpawn(MapSpawn s, HashSet<string>? ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every spawn needs a non-empty id.");
        else if (ids is not null && !ids.Add(s.Id)) errors.Add($"duplicate spawn id '{s.Id}'.");
        if (string.IsNullOrWhiteSpace(s.ArchetypeId)) errors.Add($"spawn '{s.Id}': archetypeId must be non-empty.");
    }

    static void ValidatePlayerSpawn(MapPlayerSpawn s, HashSet<string>? ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every player spawn needs a non-empty id.");
        else if (ids is not null && !ids.Add(s.Id)) errors.Add($"duplicate player spawn id '{s.Id}'.");
    }

    /// <summary>A document may declare any positive finite tile size, but never one narrower than a single
    /// sculpt tile: the origin-corner ownership rule would then assign sculpt tiles to document tiles that do
    /// not cover them.</summary>
    static void ValidateTileSize(MapDocument doc, List<string> errors)
    {
        if (!(doc.TileSize > 0f) || float.IsInfinity(doc.TileSize))
        {
            errors.Add($"tileSize must be positive and finite, got {doc.TileSize.ToString(CultureInfo.InvariantCulture)}.");
            return;
        }
        float cellSize = doc.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
        float span = TerrainSculpt.TileSize * cellSize;
        if (doc.TileSize < span)
            errors.Add($"tileSize {doc.TileSize.ToString(CultureInfo.InvariantCulture)} is narrower than one sculpt tile " +
                       $"({span.ToString(CultureInfo.InvariantCulture)} m at sculpt cell size {cellSize.ToString(CultureInfo.InvariantCulture)}).");
    }

    /// <summary>The sculpt layer's cell size must be positive, and every stored tile's world extent (its
    /// cell centers) must lie within the document bounds. This is what makes the writer refuse an
    /// out-of-bounds tile: <see cref="MapDocumentFile.Save"/> validates before writing.</summary>
    static void ValidateOverrides(MapTerrainOverrides overrides, MapBounds bounds, List<string> errors)
    {
        if (!(overrides.CellSize > 0f))
        {
            errors.Add("terrainOverrides.cellSize must be positive.");
            return;
        }
        foreach (MapSculptTile tile in overrides.Tiles) ValidateSculptTile(tile, overrides.CellSize, bounds, errors);
    }

    static void ValidateSculptTile(MapSculptTile tile, float cellSize, MapBounds bounds, List<string> errors)
    {
        const int last = TerrainSculpt.TileSize - 1;
        float minX = tile.TileX * TerrainSculpt.TileSize * cellSize;
        float minZ = tile.TileZ * TerrainSculpt.TileSize * cellSize;
        float maxX = (tile.TileX * TerrainSculpt.TileSize + last) * cellSize;
        float maxZ = (tile.TileZ * TerrainSculpt.TileSize + last) * cellSize;
        if (minX < bounds.MinX || maxX > bounds.MaxX || minZ < bounds.MinZ || maxZ > bounds.MaxZ)
            errors.Add($"terrainOverrides tile ({tile.TileX}, {tile.TileZ}) extent [{minX}..{maxX}] x [{minZ}..{maxZ}] leaves the document bounds.");
    }

    static void CheckLayerRefs(List<string>? layers, HashSet<string> known, string where, List<string> errors)
    {
        if (layers is null) return;
        foreach (string name in layers)
            if (!known.Contains(name))
                errors.Add($"{where}: layer filter references unknown scatter layer '{name}'.");
    }
}
