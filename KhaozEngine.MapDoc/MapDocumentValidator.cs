using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Semantic validation beyond what deserialization enforces. Returns human-readable errors (empty =
/// valid). <see cref="MapDocumentFile"/> runs this on every load and save and throws on any error, per the
/// loud-fail stance for dev-authored content.</summary>
public static class MapDocumentValidator
{
    public static IReadOnlyList<string> Validate(MapDocument doc, MapDocRegistry registry)
        => Validate(doc, registry, null);

    internal static IReadOnlyList<string> Validate(MapDocument doc, MapDocRegistry registry, List<MapDocumentFinding>? findings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(registry);
        var errors = new List<string>();

        if (doc.FormatVersion != MapDocumentFile.CurrentFormatVersion)
            Add(new(MapDocumentRefusal.DocumentVersionUnsupported, $"formatVersion is {doc.FormatVersion}, expected {MapDocumentFile.CurrentFormatVersion}.")
            { FieldPath = "$.formatVersion", ActualVersion = doc.FormatVersion, SupportedVersions = [MapDocumentFile.CurrentFormatVersion] });
        if (string.IsNullOrWhiteSpace(doc.Id))
            Add(new(MapDocumentRefusal.SchemaValueRequired, "id must be non-empty.") { FieldPath = "$.id", DocumentId = doc.Id });
        if (!(doc.Bounds.MaxX > doc.Bounds.MinX) || !(doc.Bounds.MaxZ > doc.Bounds.MinZ))
        {
            bool x = !(doc.Bounds.MaxX > doc.Bounds.MinX);
            float min = x ? doc.Bounds.MinX : doc.Bounds.MinZ, max = x ? doc.Bounds.MaxX : doc.Bounds.MaxZ;
            bool finite = float.IsFinite(min) && float.IsFinite(max);
            Add(new(finite ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.ValueNonfinite,
                "bounds must satisfy MaxX > MinX and MaxZ > MinZ.")
            { FieldPath = "$.bounds." + (!float.IsFinite(min) ? x ? "minX" : "minZ" : x ? "maxX" : "maxZ"),
                MinimumNumber = finite ? min : null, MaximumNumber = finite ? max : null });
        }
        MapNativeValidation.Validate(doc, errors, findings);
        MapWaterValidation.Validate(doc, errors, new MapWaterWorkBudget(), findings);
        errors.AddRange(MapPrefabValidation.Validate(doc, null, findings));
        MapFoliageValidation.ValidateDocument(doc, errors, findings);
        ValidateTileSize(doc, errors);
        if (doc.TerrainOverrides is { } overrides)
            ValidateOverrides(overrides, doc.Bounds, errors);

        var layerNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.ScatterLayers.Count; i++)
        {
            MapScatterLayer layer = doc.ScatterLayers[i];
            if (string.IsNullOrWhiteSpace(layer.Name)) { Add(Item(MapDocumentRefusal.SchemaValueRequired,
                "every scatter layer needs a non-empty name.", "scatterLayers", i, "name", layer.Name, "scatter layer")); continue; }
            if (!layerNames.Add(layer.Name)) Add(Item(MapDocumentRefusal.IdentityDuplicate,
                $"duplicate scatter layer name '{layer.Name}'.", "scatterLayers", i, "name", layer.Name, "scatter layer"));
            if (layer.CellSize <= 0f) Add(Item(float.IsFinite(layer.CellSize) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                $"scatter layer '{layer.Name}': cellSize must be positive.", "scatterLayers", i, "cellSize", layer.Name, "scatter layer") with
                { ActualNumber = float.IsFinite(layer.CellSize) ? layer.CellSize : null, MinimumNumber = 0 });
            if (layer.ScaleMax < layer.ScaleMin)
            {
                bool finite = float.IsFinite(layer.ScaleMin) && float.IsFinite(layer.ScaleMax);
                Add(Item(finite ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                    $"scatter layer '{layer.Name}': scaleMax must be >= scaleMin.", "scatterLayers", i,
                    !float.IsFinite(layer.ScaleMin) ? "scaleMin" : "scaleMax", layer.Name, "scatter layer") with
                    { ActualNumber = finite ? layer.ScaleMax : null, MinimumNumber = finite ? layer.ScaleMin : null });
            }
        }

        var companionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.CompanionLayers.Count; i++)
        {
            MapCompanionLayer layer = doc.CompanionLayers[i];
            if (string.IsNullOrWhiteSpace(layer.Name)) { Add(Item(MapDocumentRefusal.SchemaValueRequired,
                "every companion layer needs a non-empty name.", "companionLayers", i, "name", layer.Name, "companion layer")); continue; }
            if (!companionNames.Add(layer.Name)) Add(Item(MapDocumentRefusal.IdentityDuplicate,
                $"duplicate companion layer name '{layer.Name}'.", "companionLayers", i, "name", layer.Name, "companion layer"));
            if (!layerNames.Contains(layer.HostLayer))
                Add(Item(MapDocumentRefusal.ReferenceMissing,
                    $"companion layer '{layer.Name}': host layer '{layer.HostLayer}' is not a scatter layer in this document.",
                    "companionLayers", i, "hostLayer", layer.Name, "companion layer") with { LayerId = layer.HostLayer });
            if (layer.CountMax < layer.CountMin) Add(Item(MapDocumentRefusal.ValueOutsideRange,
                $"companion layer '{layer.Name}': countMax must be >= countMin.", "companionLayers", i, "countMax", layer.Name, "companion layer") with
                { ActualInteger = layer.CountMax, MinimumInteger = layer.CountMin });
        }

        var exclusionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            MapExclusion e = doc.Exclusions[i];
            if (e.Shape is null) Add(Item(MapDocumentRefusal.SchemaValueRequired, $"exclusions[{i}]: shape is required.",
                "exclusions", i, "shape", e.Name, "exclusion"));
            if (!string.IsNullOrEmpty(e.Name) && !exclusionNames.Add(e.Name))
                Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate exclusion name '{e.Name}'.",
                    "exclusions", i, "name", e.Name, "exclusion"));
            CheckLayerRefs(e.Layers, layerNames, $"exclusions[{i}]", errors);
        }

        var scatterOverrideNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            MapScatterOverrideDoc o = doc.ScatterOverrides[i];
            if (o.Shape is null) Add(Item(MapDocumentRefusal.SchemaValueRequired, $"scatterOverrides[{i}]: shape is required.",
                "scatterOverrides", i, "shape", o.Name, "scatter override"));
            if (o.DensityMultiplier < 0f) Add(Item(float.IsFinite(o.DensityMultiplier) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                $"scatterOverrides[{i}]: densityMultiplier must be >= 0.", "scatterOverrides", i, "densityMultiplier", o.Name, "scatter override") with
                { ActualNumber = float.IsFinite(o.DensityMultiplier) ? o.DensityMultiplier : null, MinimumNumber = 0 });
            if (!string.IsNullOrEmpty(o.Name) && !scatterOverrideNames.Add(o.Name))
                Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate scatter override name '{o.Name}'.",
                    "scatterOverrides", i, "name", o.Name, "scatter override"));
            CheckLayerRefs(o.Layers, layerNames, $"scatterOverrides[{i}]", errors);
        }

        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in doc.Placements) ValidatePlacement(p, placementIds, errors);

        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSpawn s in doc.Spawns) ValidateSpawn(s, spawnIds, errors);

        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlayerSpawn s in doc.PlayerSpawns) ValidatePlayerSpawn(s, playerSpawnIds, errors);

        var regionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Regions.Count; i++)
        {
            MapRegion r = doc.Regions[i];
            if (string.IsNullOrWhiteSpace(r.Name)) Add(Item(MapDocumentRefusal.SchemaValueRequired,
                "every region needs a non-empty name.", "regions", i, "name", r.Name, "region"));
            else if (!regionNames.Add(r.Name)) Add(Item(MapDocumentRefusal.IdentityDuplicate,
                $"duplicate region name '{r.Name}'.", "regions", i, "name", r.Name, "region"));
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

        void Add(MapDocumentFinding finding)
        {
            errors.Add(finding.Message);
            findings?.Add(finding);
        }
    }

    static MapDocumentFinding Item(MapDocumentRefusal reason, string message, string collection, int index,
        string field, string? id, string kind) => new(reason, message)
        { FieldPath = $"$.{collection}[{index}].{field}", Index = index, EntityId = id, SubjectKind = kind };

    /// <summary>The per-entity rules of <see cref="Validate(MapDocument, MapDocRegistry)"/> for one slice of a document's content, such as one
    /// stored tile, in the order <see cref="Validate(MapDocument, MapDocRegistry)"/> reports them. <paramref name="globals"/> carries the
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
