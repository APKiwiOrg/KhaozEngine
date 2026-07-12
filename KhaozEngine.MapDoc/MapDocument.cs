using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The root of a zone/map document: everything that describes a zone's static world content. One JSON
/// file per zone, human-diffable, git-committed in the game repo. Both game heads load the same document through
/// <see cref="MapDocumentFile"/> and build runtime objects with <see cref="MapRuntime"/>, so client and server
/// agree by construction.</summary>
public sealed class MapDocument
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public int FormatVersion { get; set; } = MapDocumentFile.CurrentFormatVersion;
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public MapBounds Bounds { get; set; } = new();
    public MapTerrain Terrain { get; set; } = new();
    public List<MapScatterLayer> ScatterLayers { get; set; } = new();
    public List<MapCompanionLayer> CompanionLayers { get; set; } = new();
    public List<MapExclusion> Exclusions { get; set; } = new();
    public List<MapScatterOverrideDoc> ScatterOverrides { get; set; } = new();
    public List<MapPlacement> Placements { get; set; } = new();
    public List<MapSpawn> Spawns { get; set; } = new();
    public List<MapRegion> Regions { get; set; } = new();

    /// <summary>Reserved for the future sculpt/delta layer. Must be absent or null in format version 1
    /// (the validator rejects anything else), so sculpting lands later as a version bump, not a break.</summary>
    public JsonNode? TerrainOverrides { get; set; }
}

/// <summary>The zone's XZ extent.</summary>
public sealed class MapBounds
{
    public float MinX { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxZ { get; set; }
}

/// <summary>Mirrors <see cref="TerrainConfig"/> (same defaults), with features as open-set DTOs and band
/// edges as nullables (null = open edge, i.e. +/- infinity, which raw JSON cannot express).</summary>
public sealed class MapTerrain
{
    public int Seed { get; set; } = 1;
    public float WaterLevel { get; set; }
    public float BiomeBlend { get; set; } = 24f;
    public float GentleFrequency { get; set; } = 0.02f;
    public float GentleAmplitude { get; set; } = 1.5f;
    public float DetailFrequency { get; set; } = 0.03f;
    public int DetailOctaves { get; set; } = 4;
    public List<MapBiomeBand> Biomes { get; set; } = new();
    public List<MapFeature> Features { get; set; } = new();
}

/// <summary>Mirrors <see cref="BiomeBand"/>. Null Start/End mean an open edge.</summary>
public sealed class MapBiomeBand
{
    public float? Start { get; set; }
    public float? End { get; set; }
    public BiomeId Biome { get; set; } = BiomeId.Meadow;
    public float BaseHeight { get; set; }
    public float HillAmplitude { get; set; }
}

/// <summary>One weighted kit id (mirrors <see cref="PropKind"/>).</summary>
public sealed class MapPropKind
{
    public string Id { get; set; } = "";
    public float Weight { get; set; } = 1f;
}

/// <summary>Mirrors <see cref="BiomeScatterRule"/>.</summary>
public sealed class MapBiomeScatterRule
{
    public BiomeId Biome { get; set; } = BiomeId.Meadow;
    public float Density { get; set; } = 0.55f;
    public List<MapPropKind> Kinds { get; set; } = new();
}

/// <summary>A named procedural scatter layer (mirrors <see cref="ScatterConfig"/> minus the legacy clearing
/// disc, which map documents express as exclusion shapes instead).</summary>
public sealed class MapScatterLayer
{
    public string Name { get; set; } = "";
    public int Seed { get; set; } = 1337;
    public float CellSize { get; set; } = 4.5f;
    public float Jitter { get; set; } = 1.6f;
    public float? MaxHeight { get; set; }
    public float ScaleMin { get; set; } = 0.8f;
    public float ScaleMax { get; set; } = 1.35f;
    public List<MapBiomeScatterRule> Rules { get; set; } = new();
}

/// <summary>A named companion layer ringing hosts from a scatter layer (mirrors <see cref="CompanionConfig"/>).</summary>
public sealed class MapCompanionLayer
{
    public string Name { get; set; } = "";
    /// <summary>The scatter layer whose placements host these companions.</summary>
    public string HostLayer { get; set; } = "";
    public int Seed { get; set; } = 1337;
    public List<string> HostKinds { get; set; } = new();
    public List<MapPropKind> Kinds { get; set; } = new();
    public int CountMin { get; set; } = 2;
    public int CountMax { get; set; } = 4;
    public float RadiusMin { get; set; } = 0.6f;
    public float RadiusMax { get; set; } = 1.8f;
    public float ScaleMin { get; set; } = 0.7f;
    public float ScaleMax { get; set; } = 1.1f;
    public float? MaxHeight { get; set; }
}

/// <summary>A region kept free of procedural scatter. Null Layers = applies to every scatter layer.</summary>
public sealed class MapExclusion
{
    /// <summary>Optional display name, unique among exclusions when set (validator-enforced). Null or empty
    /// means unnamed, and the editor falls back to an index-based label ("exclusion[i]"). Serialized only when
    /// set: the document's global WhenWritingNull option omits a null Name, so an unnamed exclusion does not
    /// bloat every document with an empty name key.</summary>
    public string? Name { get; set; }
    public MapShapeDoc? Shape { get; set; }
    public List<string>? Layers { get; set; }
}

/// <summary>A region-scoped scatter tweak (density multiplier and/or kind substitution). Null Layers =
/// applies to every scatter layer. First matching override wins (document order).</summary>
public sealed class MapScatterOverrideDoc
{
    public MapShapeDoc? Shape { get; set; }
    public float DensityMultiplier { get; set; } = 1f;
    public List<MapPropKind>? Kinds { get; set; }
    public List<string>? Layers { get; set; }
}

/// <summary>An authored prop/building placement. Null Y = ground-snap to the terrain field at load.</summary>
public sealed class MapPlacement
{
    /// <summary>Stable editor identity, unique within the document.</summary>
    public string Id { get; set; } = "";
    /// <summary>The asset-manifest kit id to instance.</summary>
    public string Kind { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float? Y { get; set; }
    public float Yaw { get; set; }
    public float Scale { get; set; } = 1f;
    public List<string> Tags { get; set; } = new();
}

/// <summary>An NPC spawn marker. The game interprets ArchetypeId.</summary>
public sealed class MapSpawn
{
    /// <summary>Stable editor identity, unique within the document.</summary>
    public string Id { get; set; } = "";
    public string ArchetypeId { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = new();
}

/// <summary>A named, tagged shape the game interprets (quest areas, safe zones, triggers).</summary>
public sealed class MapRegion
{
    public string Name { get; set; } = "";
    public MapShapeDoc? Shape { get; set; }
    public List<string> Tags { get; set; } = new();
}
