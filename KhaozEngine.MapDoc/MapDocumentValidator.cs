using System;
using System.Collections.Generic;

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
        if (doc.TerrainOverrides is not null)
            errors.Add("terrainOverrides is reserved for a future format version and must be absent or null.");

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

        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            MapScatterOverrideDoc o = doc.ScatterOverrides[i];
            if (o.Shape is null) errors.Add($"scatterOverrides[{i}]: shape is required.");
            if (o.DensityMultiplier < 0f) errors.Add($"scatterOverrides[{i}]: densityMultiplier must be >= 0.");
            CheckLayerRefs(o.Layers, layerNames, $"scatterOverrides[{i}]", errors);
        }

        var placementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in doc.Placements)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) errors.Add("every placement needs a non-empty id.");
            else if (!placementIds.Add(p.Id)) errors.Add($"duplicate placement id '{p.Id}'.");
            if (string.IsNullOrWhiteSpace(p.Kind)) errors.Add($"placement '{p.Id}': kind must be non-empty.");
            if (p.Scale <= 0f) errors.Add($"placement '{p.Id}': scale must be positive.");
        }

        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSpawn s in doc.Spawns)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every spawn needs a non-empty id.");
            else if (!spawnIds.Add(s.Id)) errors.Add($"duplicate spawn id '{s.Id}'.");
            if (string.IsNullOrWhiteSpace(s.ArchetypeId)) errors.Add($"spawn '{s.Id}': archetypeId must be non-empty.");
        }

        var playerSpawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("every player spawn needs a non-empty id.");
            else if (!playerSpawnIds.Add(s.Id)) errors.Add($"duplicate player spawn id '{s.Id}'.");
        }

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

    static void CheckLayerRefs(List<string>? layers, HashSet<string> known, string where, List<string> errors)
    {
        if (layers is null) return;
        foreach (string name in layers)
            if (!known.Contains(name))
                errors.Add($"{where}: layer filter references unknown scatter layer '{name}'.");
    }
}
