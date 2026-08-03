using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Builds the runtime objects games already consume from a validated <see cref="MapDocument"/>.
/// Both heads call the same builders, so client and server agree by construction. Ground-snapping uses the
/// deterministic field, keeping authored placements consistent everywhere.</summary>
public static class MapRuntime
{
    /// <summary>The document's terrain section as an engine <see cref="TerrainConfig"/> (features built
    /// through the registry, null band edges mapped to +/- infinity).</summary>
    public static TerrainConfig BuildTerrainConfig(MapDocument doc, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(registry);
        MapTerrain t = doc.Terrain;

        ITerrainFeature[]? features = null;
        if (t.Features.Count > 0)
        {
            features = new ITerrainFeature[t.Features.Count];
            for (int i = 0; i < t.Features.Count; i++)
                features[i] = registry.BuildFeature(t.Features[i]);
        }

        BiomeBand[]? bands = null;
        if (t.Biomes.Count > 0)
        {
            bands = new BiomeBand[t.Biomes.Count];
            for (int i = 0; i < t.Biomes.Count; i++)
            {
                MapBiomeBand b = t.Biomes[i];
                bands[i] = new BiomeBand
                {
                    Start = b.Start ?? float.NegativeInfinity,
                    End = b.End ?? float.PositiveInfinity,
                    Biome = b.Biome,
                    BaseHeight = b.BaseHeight,
                    HillAmplitude = b.HillAmplitude,
                };
            }
        }

        return new TerrainConfig
        {
            Seed = t.Seed,
            WaterLevel = t.WaterLevel,
            BiomeBlend = t.BiomeBlend,
            GentleFrequency = t.GentleFrequency,
            GentleAmplitude = t.GentleAmplitude,
            DetailFrequency = t.DetailFrequency,
            DetailOctaves = t.DetailOctaves,
            Biomes = bands,
            Features = features,
        };
    }

    /// <summary>Builds the terrain field for the document, folding in the authored sculpt layer
    /// (<see cref="MapDocument.TerrainOverrides"/>) when the document has one. With no overrides the field
    /// takes its pure-analytic fast path, so every current terrain consumer inherits authored terrain with
    /// no signature change.</summary>
    public static TerrainField BuildField(MapDocument doc, MapDocRegistry registry)
    {
        TerrainConfig config = BuildTerrainConfig(doc, registry);
        return new TerrainField(config, BuildSculpt(doc.TerrainOverrides));
    }

    /// <summary>The runtime sculpt layer for the document's override block, or null when there is no
    /// sculpting (an absent or empty block), so the field keeps its analytic fast path.</summary>
    public static TerrainSculpt? BuildSculpt(MapTerrainOverrides? overrides)
    {
        if (overrides is null || overrides.IsEmpty) return null;
        var tiles = new List<TerrainSculptTile>(overrides.TileCount);
        foreach (MapSculptTile tile in overrides.Tiles)
            tiles.Add(new TerrainSculptTile(tile.TileX, tile.TileZ, tile.Deltas));
        return new TerrainSculpt(overrides.CellSize, tiles);
    }

    /// <summary>One scatter layer as an engine <see cref="ScatterConfig"/>. The legacy clearing disc is zeroed
    /// (documents author clearings as exclusion shapes), and only the exclusions/overrides whose layer filter
    /// is null or names this layer are attached.</summary>
    public static ScatterConfig BuildScatterConfig(MapDocument doc, string layerName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapScatterLayer? layer = doc.ScatterLayers.Find(l => l.Name == layerName)
            ?? throw new MapDocumentException($"unknown scatter layer '{layerName}' in map '{doc.Id}'.");

        var rules = new BiomeScatterRule[layer.Rules.Count];
        for (int i = 0; i < layer.Rules.Count; i++)
        {
            MapBiomeScatterRule r = layer.Rules[i];
            rules[i] = new BiomeScatterRule { Biome = r.Biome, Density = r.Density, Kinds = ToKinds(r.Kinds) };
        }

        var exclusions = new List<IArea2D>();
        foreach (MapExclusion e in doc.Exclusions)
            if (e.Shape is not null && AppliesTo(e.Layers, layerName))
                exclusions.Add(e.Shape.ToArea());

        var overrides = new List<ScatterOverride>();
        foreach (MapScatterOverrideDoc o in doc.ScatterOverrides)
            if (o.Shape is not null && AppliesTo(o.Layers, layerName))
                overrides.Add(new ScatterOverride
                {
                    Area = o.Shape.ToArea(),
                    DensityMultiplier = o.DensityMultiplier,
                    Kinds = o.Kinds is { Count: > 0 } ? ToKinds(o.Kinds) : null,
                });

        return new ScatterConfig
        {
            Seed = layer.Seed,
            CellSize = layer.CellSize,
            Jitter = layer.Jitter,
            ClearingRadius = 0f,
            MaxHeight = layer.MaxHeight,
            ScaleMin = layer.ScaleMin,
            ScaleMax = layer.ScaleMax,
            Biomes = rules,
            Exclusions = exclusions.ToArray(),
            Overrides = overrides.ToArray(),
        };
    }

    /// <summary>Every scatter layer, keyed by name.</summary>
    public static IReadOnlyDictionary<string, ScatterConfig> BuildScatterConfigs(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var result = new Dictionary<string, ScatterConfig>(StringComparer.Ordinal);
        foreach (MapScatterLayer layer in doc.ScatterLayers)
            result[layer.Name] = BuildScatterConfig(doc, layer.Name);
        return result;
    }

    /// <summary>One companion layer as an engine <see cref="CompanionConfig"/>. The host placements come from
    /// the layer named by <see cref="MapCompanionLayer.HostLayer"/> (query it via
    /// <see cref="BuildScatterConfig"/> and <see cref="PropScatter.Generate"/>).</summary>
    public static CompanionConfig BuildCompanionConfig(MapDocument doc, string companionLayerName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapCompanionLayer? layer = doc.CompanionLayers.Find(l => l.Name == companionLayerName)
            ?? throw new MapDocumentException($"unknown companion layer '{companionLayerName}' in map '{doc.Id}'.");
        return new CompanionConfig
        {
            Seed = layer.Seed,
            HostKinds = layer.HostKinds.ToArray(),
            Kinds = ToKinds(layer.Kinds),
            CountMin = layer.CountMin,
            CountMax = layer.CountMax,
            RadiusMin = layer.RadiusMin,
            RadiusMax = layer.RadiusMax,
            ScaleMin = layer.ScaleMin,
            ScaleMax = layer.ScaleMax,
            MaxHeight = layer.MaxHeight,
        };
    }

    /// <summary>The document's regions as a resolved, point-testable <see cref="MapRegionSet"/>: shapes
    /// converted to their runtime areas once, document order preserved, and shapeless entries skipped the same
    /// way the scatter builder skips them. The editor's overlay picking runs on this too, so authored regions
    /// resolve identically at edit time and at run time.</summary>
    public static MapRegionSet BuildRegions(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return MapRegionSet.Build(doc);
    }

    /// <summary>Authored placements as engine <see cref="PropPlacement"/>s. Null Y ground-snaps to the field
    /// (deterministic, so every head agrees). Variant is always 0 for authored placements.</summary>
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapDocument doc, TerrainField field)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(field);
        var result = new List<PropPlacement>(doc.Placements.Count);
        foreach (MapPlacement p in doc.Placements) Add(result, p, field);
        return result;
    }

    /// <summary>Authored placements whose (X, Z) falls in the HALF-OPEN rect. A partition of rects covering
    /// the world reproduces the whole-document result exactly, including ground-snap, because snapping
    /// samples the deterministic field per placement and never depends on neighbours. Half-open on both axes
    /// matches <c>ChunkGrid.AreaOf</c>'s streaming invariant: a placement exactly on a rect's max edge
    /// belongs to the next rect, and that is what makes the partition reproduce the whole.</summary>
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapDocument doc, TerrainField field, RectArea area)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(field);
        var result = new List<PropPlacement>();
        foreach (MapPlacement p in doc.Placements)
            if (MapSpatialIndex.InArea(p.X, p.Z, area)) Add(result, p, field);
        return result;
    }

    /// <summary>O(k) form over a prebuilt index: one document tile's authored placements.</summary>
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapSpatialIndex index, TerrainField field, MapTileCoord tile)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(field);
        IReadOnlyList<MapPlacement> bucket = index.PlacementsIn(tile);
        var result = new List<PropPlacement>(bucket.Count);
        foreach (MapPlacement p in bucket) Add(result, p, field);
        return result;
    }

    /// <summary>Allocation-free form: appends into a caller-owned list.</summary>
    public static void BuildPlacements(MapSpatialIndex index, TerrainField field, MapTileCoord tile, List<PropPlacement> into)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(into);
        foreach (MapPlacement p in index.PlacementsIn(tile)) Add(into, p, field);
    }

    static void Add(List<PropPlacement> into, MapPlacement p, TerrainField field)
    {
        float y = p.Y ?? field.SampleHeight(p.X, p.Z);
        into.Add(new PropPlacement(p.Kind, p.X, y, p.Z, p.Scale, p.Yaw, 0));
    }

    static PropKind[] ToKinds(List<MapPropKind> kinds)
    {
        var result = new PropKind[kinds.Count];
        for (int i = 0; i < kinds.Count; i++) result[i] = new PropKind(kinds[i].Id, kinds[i].Weight);
        return result;
    }

    static bool AppliesTo(List<string>? layers, string layerName)
        => layers is null || layers.Contains(layerName);
}
