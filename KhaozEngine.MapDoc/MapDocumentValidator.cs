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

    /// <summary>Returns the ordered immutable known typed findings behind <see cref="Validate(MapDocument, MapDocRegistry)"/>.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(MapDocument doc, MapDocRegistry registry)
    {
        var findings = new List<MapDocumentFinding>();
        _ = Validate(doc, registry, findings);
        return findings.AsReadOnly();
    }

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
        ValidateTileSize(doc, errors, findings);
        if (doc.TerrainOverrides is { } overrides)
            ValidateOverrides(overrides, doc.Bounds, errors, findings);

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
            CheckLayerRefs(e.Layers, layerNames, $"exclusions[{i}]", e.Name, "exclusion", i, errors, findings);
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
            CheckLayerRefs(o.Layers, layerNames, $"scatterOverrides[{i}]", o.Name, "scatter override", i, errors, findings);
        }

        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Placements.Count; i++) ValidatePlacement(doc.Placements[i], placementIds, i, errors, findings);

        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Spawns.Count; i++) ValidateSpawn(doc.Spawns[i], spawnIds, i, errors, findings);

        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.PlayerSpawns.Count; i++) ValidatePlayerSpawn(doc.PlayerSpawns[i], playerSpawnIds, i, errors, findings);

        var regionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Regions.Count; i++)
        {
            MapRegion r = doc.Regions[i];
            if (string.IsNullOrWhiteSpace(r.Name)) Add(Item(MapDocumentRefusal.SchemaValueRequired,
                "every region needs a non-empty name.", "regions", i, "name", r.Name, "region"));
            else if (!regionNames.Add(r.Name)) Add(Item(MapDocumentRefusal.IdentityDuplicate,
                $"duplicate region name '{r.Name}'.", "regions", i, "name", r.Name, "region"));
            if (r.Shape is null) Add(Item(MapDocumentRefusal.SchemaValueRequired, $"region '{r.Name}': shape is required.",
                "regions", i, "shape", r.Name, "region"));
        }

        var featureNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Terrain.Features.Count; i++)
        {
            MapFeature f = doc.Terrain.Features[i];
            string type = f.Type;
            if (!registry.TryGetFeatureDocType(type, out _))
                Add(Item(MapDocumentRefusal.DocumentTerrainFeatureUnknown,
                    $"terrain feature type '{type}' is not registered on the MapDocRegistry.",
                    "terrain.features", i, "type", f.Name, "terrain feature") with { ActualText = type });
            if (!string.IsNullOrEmpty(f.Name) && !featureNames.Add(f.Name))
                Add(Item(MapDocumentRefusal.IdentityDuplicate, $"duplicate terrain feature name '{f.Name}'.",
                    "terrain.features", i, "name", f.Name, "terrain feature"));
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
        MapTileContent content, HashSet<long> numericIds, List<MapDocumentFinding>? findings = null)
    {
        var errors = new List<string>();
        for (int i = 0; i < content.Placements.Count; i++)
            MapNativeValidation.ValidateNumericId(content.Placements[i], globals.NumericIdHighWaterMark, numericIds, errors,
                findings: findings, fieldPath: $"$.placements[{i}]");
        for (int i = 0; i < content.Placements.Count; i++) MapNativeValidation.ValidateSupportBinding(content.Placements[i], surfaces, errors,
            findings, $"$.placements[{i}]");
        if (globals.TerrainOverrides is { } overrides)
            for (int i = 0; i < content.SculptTiles.Count; i++) ValidateSculptTile(content.SculptTiles[i], overrides.CellSize, globals.Bounds, i, errors, findings);
        for (int i = 0; i < content.Placements.Count; i++) ValidatePlacement(content.Placements[i], null, i, errors, findings);
        for (int i = 0; i < content.Spawns.Count; i++) ValidateSpawn(content.Spawns[i], null, i, errors, findings);
        for (int i = 0; i < content.PlayerSpawns.Count; i++) ValidatePlayerSpawn(content.PlayerSpawns[i], null, i, errors, findings);
        return errors;
    }

    // A null ID set skips the duplicate check, for a caller that checks uniqueness another way.
    static void ValidatePlacement(MapPlacement p, HashSet<string>? ids, int index, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (string.IsNullOrWhiteSpace(p.Id)) Add(errors, findings, Placement(MapDocumentRefusal.SchemaValueRequired, "every placement needs a non-empty id.", "id"));
        else if (ids is not null && !ids.Add(p.Id)) Add(errors, findings, Placement(MapDocumentRefusal.IdentityDuplicate, $"duplicate placement id '{p.Id}'.", "id"));
        if (string.IsNullOrWhiteSpace(p.Kind)) Add(errors, findings, Placement(MapDocumentRefusal.SchemaValueRequired, $"placement '{p.Id}': kind must be non-empty.", "kind"));
        if (p.Scale <= 0f) Add(errors, findings, Placement(float.IsFinite(p.Scale) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
            $"placement '{p.Id}': scale must be positive.", "scale") with { ActualNumber = float.IsFinite(p.Scale) ? p.Scale : null, MinimumNumber = 0 });

        MapDocumentFinding Placement(MapDocumentRefusal reason, string message, string field) =>
            Item(reason, message, "placements", index, field, p.Id, "placement") with { PlacementId = p.Id };
    }

    static void ValidateSpawn(MapSpawn s, HashSet<string>? ids, int index, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) Add(errors, findings, Spawn(MapDocumentRefusal.SchemaValueRequired, "every spawn needs a non-empty id.", "id"));
        else if (ids is not null && !ids.Add(s.Id)) Add(errors, findings, Spawn(MapDocumentRefusal.IdentityDuplicate, $"duplicate spawn id '{s.Id}'.", "id"));
        if (string.IsNullOrWhiteSpace(s.ArchetypeId)) Add(errors, findings, Spawn(MapDocumentRefusal.SchemaValueRequired, $"spawn '{s.Id}': archetypeId must be non-empty.", "archetypeId"));

        MapDocumentFinding Spawn(MapDocumentRefusal reason, string message, string field) => Item(reason, message, "spawns", index, field, s.Id, "spawn");
    }

    static void ValidatePlayerSpawn(MapPlayerSpawn s, HashSet<string>? ids, int index, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) Add(errors, findings, Item(MapDocumentRefusal.SchemaValueRequired,
            "every player spawn needs a non-empty id.", "playerSpawns", index, "id", s.Id, "player spawn"));
        else if (ids is not null && !ids.Add(s.Id)) Add(errors, findings, Item(MapDocumentRefusal.IdentityDuplicate,
            $"duplicate player spawn id '{s.Id}'.", "playerSpawns", index, "id", s.Id, "player spawn"));
    }

    /// <summary>A document may declare any positive finite tile size, but never one narrower than a single
    /// sculpt tile: the origin-corner ownership rule would then assign sculpt tiles to document tiles that do
    /// not cover them.</summary>
    static void ValidateTileSize(MapDocument doc, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (!(doc.TileSize > 0f) || float.IsInfinity(doc.TileSize))
        {
            Add(errors, findings, new(float.IsFinite(doc.TileSize) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                $"tileSize must be positive and finite, got {doc.TileSize.ToString(CultureInfo.InvariantCulture)}.")
            { FieldPath = "$.tileSize", ActualNumber = float.IsFinite(doc.TileSize) ? doc.TileSize : null, MinimumNumber = 0 });
            return;
        }
        float cellSize = doc.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
        float span = TerrainSculpt.TileSize * cellSize;
        if (doc.TileSize < span)
            Add(errors, findings, new(MapDocumentRefusal.BoundsInvalid, $"tileSize {doc.TileSize.ToString(CultureInfo.InvariantCulture)} is narrower than one sculpt tile " +
                       $"({span.ToString(CultureInfo.InvariantCulture)} m at sculpt cell size {cellSize.ToString(CultureInfo.InvariantCulture)}).")
            { FieldPath = "$.tileSize", ActualNumber = doc.TileSize, MinimumNumber = span });
    }

    /// <summary>The sculpt layer's cell size must be positive, and every stored tile's world extent (its
    /// cell centers) must lie within the document bounds. This is what makes the writer refuse an
    /// out-of-bounds tile: <see cref="MapDocumentFile.Save"/> validates before writing.</summary>
    static void ValidateOverrides(MapTerrainOverrides overrides, MapBounds bounds, List<string> errors, List<MapDocumentFinding>? findings)
    {
        float cellSize = overrides.CellSize;
        if (OverrideCellSizeFailure(cellSize) is { } finding)
        {
            Add(errors, findings, finding);
            return;
        }
        var tiles = overrides.Tiles;
        for (int i = 0; i < tiles.Count; i++) ValidateSculptTile(tiles[i], cellSize, bounds, i, errors, findings);
    }

    // Construction admits only positive finite values. This fault seam reaches the same defensive guard.
    internal static MapDocumentFinding? InvalidOverrideCellSizeForTests(float cellSize) => OverrideCellSizeFailure(cellSize);
    static MapDocumentFinding? OverrideCellSizeFailure(float cellSize) => !(cellSize > 0f)
        ? new(float.IsFinite(cellSize) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
            "terrainOverrides.cellSize must be positive.")
        { FieldPath = "$.terrainOverrides.cellSize", ActualNumber = float.IsFinite(cellSize) ? cellSize : null, MinimumNumber = 0 } : null;

    static void ValidateSculptTile(MapSculptTile tile, float cellSize, MapBounds bounds, int index, List<string> errors, List<MapDocumentFinding>? findings)
    {
        const int last = TerrainSculpt.TileSize - 1;
        float minX = tile.TileX * TerrainSculpt.TileSize * cellSize;
        float minZ = tile.TileZ * TerrainSculpt.TileSize * cellSize;
        float maxX = (tile.TileX * TerrainSculpt.TileSize + last) * cellSize;
        float maxZ = (tile.TileZ * TerrainSculpt.TileSize + last) * cellSize;
        if (minX < bounds.MinX || maxX > bounds.MaxX || minZ < bounds.MinZ || maxZ > bounds.MaxZ)
        {
            string field = minX < bounds.MinX ? "minX" : maxX > bounds.MaxX ? "maxX" : minZ < bounds.MinZ ? "minZ" : "maxZ";
            bool x = field is "minX" or "maxX";
            float actual = field == "minX" ? minX : field == "maxX" ? maxX : field == "minZ" ? minZ : maxZ;
            Add(errors, findings, new(float.IsFinite(actual) ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.ValueNonfinite,
                $"terrainOverrides tile ({tile.TileX}, {tile.TileZ}) extent [{minX}..{maxX}] x [{minZ}..{maxZ}] leaves the document bounds.")
            { SculptTile = new(tile.TileX, tile.TileZ), Index = index, FieldPath = $"$.terrainOverrides.tiles[{index}].{field}",
                ActualNumber = float.IsFinite(actual) ? actual : null,
                MinimumNumber = x ? bounds.MinX : bounds.MinZ, MaximumNumber = x ? bounds.MaxX : bounds.MaxZ });
        }
    }

    static void CheckLayerRefs(List<string>? layers, HashSet<string> known, string where, string? id, string kind,
        int index, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layers is null) return;
        for (int i = 0; i < layers.Count; i++)
        {
            string name = layers[i];
            if (!known.Contains(name))
                Add(errors, findings, new(MapDocumentRefusal.ReferenceMissing, $"{where}: layer filter references unknown scatter layer '{name}'.")
                { FieldPath = $"$.{where}.layers[{i}]", EntityId = id, SubjectKind = kind, Index = index, LayerId = name });
        }
    }

    static void Add(List<string> errors, List<MapDocumentFinding>? findings, MapDocumentFinding finding)
    {
        errors.Add(finding.Message);
        findings?.Add(finding);
    }
}
