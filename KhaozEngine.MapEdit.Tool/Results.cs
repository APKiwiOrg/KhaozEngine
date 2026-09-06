using System.Collections.Generic;

namespace KhaozEngine.MapEdit;

/// <summary>Result of opening or creating a document: the resolved path, its identity, and a full summary.</summary>
public sealed record OpenResult(string Path, string Id, string DisplayName, MapSummary Summary);

/// <summary>Result of a save: the path written and whether it was saved.</summary>
public sealed record SaveResult(string Path, bool Saved);

/// <summary>The loaded window of the open document: <see cref="Tiled"/> false for a monolithic or in-memory
/// document, in which case every other field is its default. <see cref="Windowed"/> is the tiled document's
/// own <c>MapTileIndex.IsPartial</c>: true only when at least one occupied tile is not currently loaded. The
/// tile-coordinate and world-coordinate rect fields are null when the whole world is loaded (including a
/// whole-loaded, unwindowed tiled document), never a scanned whole-document extent. <see cref="OccupiedCount"/>
/// and <see cref="LoadedCount"/> are always the document's tile index totals regardless of windowing.</summary>
public sealed record WindowStatusResult(bool Tiled, bool Windowed,
    int? MinTileX, int? MinTileZ, int? MaxTileX, int? MaxTileZ,
    float? MinX, float? MinZ, float? MaxX, float? MaxZ,
    int OccupiedCount, int LoadedCount);

/// <summary>Result of an explicit form conversion (<c>convert_to_tiled</c> / <c>convert_to_single</c>): the
/// path written, the form written in ("Tiled" or "Monolithic"), the preserved <c>MapDocument.TileSize</c>, and
/// the world hash, which a conversion never changes (the same world, a different on-disk layout).</summary>
public sealed record ConvertResult(string Path, string Form, float TileSize, string WorldHash);

/// <summary>Result of <c>retile</c>: the path re-saved, the new tile size, the world hash before and after, and
/// a warning that states plainly whether (and how) the world hash changed, since <c>tileSize</c> is part of
/// world identity and a re-tile that changed it needs a coordinated client and server release.</summary>
public sealed record RetileResult(string Path, float TileSize, string OldWorldHash, string NewWorldHash, string Warning);

/// <summary>Result of validation. <see cref="SchemaScope"/> is <c>document</c> for a whole document,
/// <c>loadedTiles</c> for a windowed tiled document, and <c>none</c> when structural errors prevented schema
/// validation. Whole-world fields describe the optional on-disk <c>VerifyTiled</c> pass. <see cref="Valid"/>
/// includes that pass only when the caller requested it.</summary>
public sealed record ValidateResult(bool StructuralValid, IReadOnlyList<string> StructuralErrors,
    bool SchemaChecked, bool SchemaValid, IReadOnlyList<string> SchemaErrors)
{
    public bool Valid { get; init; }
    public string SchemaScope { get; init; } = "none";
    public bool WholeWorldChecked { get; init; }
    public bool WholeWorldValid { get; init; }
    public IReadOnlyList<string> WholeWorldErrors { get; init; } = System.Array.Empty<string>();
}

/// <summary>A flat snapshot of the open document: identity, bounds, terrain seed and water level, the feature
/// types in fold order, layer and companion names, section counts, the player spawn ids, region names, and the
/// dirty flag. Kept flat so it serializes cleanly to the MCP client.</summary>
public sealed record MapSummary(string Id, string DisplayName, int FormatVersion,
    float MinX, float MinZ, float MaxX, float MaxZ,
    int Seed, float WaterLevel,
    IReadOnlyList<string> FeatureTypes,
    IReadOnlyList<string> ScatterLayers, IReadOnlyList<string> CompanionLayers,
    int ExclusionCount, int ScatterOverrideCount,
    int PlacementCount, int SpawnCount, int PlayerSpawnCount, IReadOnlyList<string> PlayerSpawnIds,
    IReadOnlyList<string> RegionNames,
    bool Dirty);

/// <summary>Ground height, slope, and water depth sampled at a single world point.</summary>
public sealed record GroundInfo(float X, float Z, float Height, float SlopeDegrees, float WaterLevel, bool BelowWater);

/// <summary>Whether a world point is walkable. Composes the engine's slope-only walkability gate with a water
/// gate, since submerged ground is never walkable regardless of slope.</summary>
public sealed record WalkableInfo(float X, float Z, bool Walkable, float SlopeDegrees, float MaxSlopeDegrees, bool BelowWater);

/// <summary>One authored placement resolved for a query. <see cref="Y"/> is the placement's explicit value when
/// set, otherwise the field's sampled ground height at (<see cref="X"/>, <see cref="Z"/>). <see cref="ExplicitY"/>
/// flags which one it is.</summary>
public sealed record PlacementEntry(string Id, string Kind, float X, float Y, float Z, float Yaw, float Scale,
    bool ExplicitY, IReadOnlyList<string> Tags);

/// <summary>One authored spawn resolved for a query. <see cref="GroundY"/> is always the field's sampled ground
/// height, since spawns have no explicit Y.</summary>
public sealed record SpawnEntry(string Id, string ArchetypeId, float X, float GroundY, float Z, bool Enabled,
    IReadOnlyList<string> Tags);

/// <summary>One authored player spawn resolved for a query. <see cref="GroundY"/> is always the field's sampled
/// ground height, since player spawns have no explicit Y, the same convention <see cref="SpawnEntry"/> uses.
/// <see cref="Yaw"/> is the facing at spawn, in radians, the field NPC spawns do not carry.</summary>
public sealed record PlayerSpawnEntry(string Id, float X, float GroundY, float Z, float Yaw, bool Enabled,
    IReadOnlyList<string> Tags);

/// <summary>Placements, NPC spawns, and player spawns whose position falls inside a query rect (inclusive
/// bounds).</summary>
public sealed record PlacementsInRectResult(IReadOnlyList<PlacementEntry> Placements, IReadOnlyList<SpawnEntry> Spawns,
    IReadOnlyList<PlayerSpawnEntry> PlayerSpawns);

/// <summary>One generated scatter prop instance, previewed but not baked into a placement.</summary>
public sealed record ScatterEntry(string Kind, float X, float Y, float Z, float Yaw, float Scale);

/// <summary>A scatter layer preview over a rect: <see cref="Total"/> is the full generated count,
/// <see cref="Entries"/> is capped at the caller's maxResults with <see cref="Truncated"/> flagging the cap.</summary>
public sealed record ScatterPreviewResult(string Layer, int Total, bool Truncated, IReadOnlyList<ScatterEntry> Entries);

/// <summary>One candidate flat spot found by <see cref="QueryService.FindFlatArea"/>: its ground height, the
/// worst (max) slope among its sampled points, and the spread between their highest and lowest sampled height.</summary>
public sealed record FlatSpot(float X, float Z, float GroundHeight, float MaxSlopeDegrees, float HeightSpread);

/// <summary>Flat spots found by a brute-force grid search at the given radius, sorted by max slope ascending,
/// then height spread ascending, then X, then Z.</summary>
public sealed record FlatAreaResult(float Radius, IReadOnlyList<FlatSpot> Spots);

/// <summary>Result of a mutation: the verb name, a short human-readable detail sentence, and whether it affected
/// the streamed world (terrain or scatter inputs), mirroring the underlying command's AffectsWorld flag.
/// <see cref="GroundY"/> is set for placement_add, the resolved ground height at the placement's XZ (reported
/// even when the placement itself keeps a null Y for live ground snap). <see cref="Id"/> carries an
/// auto-generated id or a renamed id/name. <see cref="Index"/> is reserved for list-position verbs added later
/// (terrain features, exclusions, scatter overrides).</summary>
public sealed record MutationResult(string Verb, string Detail, bool WorldChanged,
    float? GroundY = null, string? Id = null, int? Index = null);

/// <summary>Result of a region bake: the layer frozen, how many scatter props became authored placements, the ids
/// of those baked placements (each <c>baked-&lt;layer&gt;-N</c>), and whether a covering exclusion was added to
/// stop the frozen props from being re-scattered on top of themselves.</summary>
public sealed record BakeResult(string Layer, int BakedCount, IReadOnlyList<string> BakedIds, bool ExclusionAdded);

/// <summary>Result of a whole-zone freeze: how many placements were frozen from the zone's procedural scatter, and
/// how many of each procedural collection (scatter layers, companion layers, exclusions, scatter overrides) were
/// removed to leave a placements-only document. <see cref="Applied"/> is false when there was nothing to freeze (the
/// document already had no scatter or companion layers), in which case every count is zero and the document is
/// untouched.</summary>
public sealed record FreezeZoneResult(int PlacementCount, int ScatterLayersRemoved, int CompanionLayersRemoved,
    int ExclusionsRemoved, int ScatterOverridesRemoved, bool Applied);

/// <summary>The terrain globals: the water level and noise seed plus the biome-blend and gentle/detail noise
/// scalars the widened <c>terrain_edit</c> verb now carries.</summary>
public sealed record TerrainInfo(int Seed, float WaterLevel, float BiomeBlend, float GentleFrequency,
    float GentleAmplitude, float DetailFrequency, int DetailOctaves);

/// <summary>One terrain biome band read back for <see cref="ProceduralInfo"/>. <see cref="Index"/> is the band's
/// list position (bands are index-addressed, the same key <c>biome_band_edit</c>/<c>biome_band_remove</c> take).
/// <see cref="Start"/>/<see cref="End"/> null means an open (unbounded) edge. <see cref="Biome"/> is the
/// <see cref="KhaozEngine.Terrain.BiomeId"/> name (for example "Meadow"), the same spelling
/// <c>biome_band_add</c>/<c>biome_band_edit</c> accept.</summary>
public sealed record BiomeBandInfo(int Index, float? Start, float? End, string Biome, float BaseHeight,
    float HillAmplitude);

/// <summary>One biome scatter rule read back inside a <see cref="ScatterLayerInfo"/>. <see cref="Kinds"/> is a
/// list of <c>"id"</c> (weight 1) / <c>"id:weight"</c> entries, the same convention the mutation verbs' kinds
/// parameters parse with <c>ParseKinds</c>.</summary>
public sealed record ScatterRuleInfo(string Biome, float Density, IReadOnlyList<string> Kinds);

/// <summary>A named procedural scatter layer read back for <see cref="ProceduralInfo"/>, full field fidelity
/// including its rules, which the <c>scatter_rule_add</c>/<c>scatter_rule_edit</c>/<c>scatter_rule_remove</c>
/// verbs write and this read path reports back regardless of whether they were set through MCP or the GUI.</summary>
public sealed record ScatterLayerInfo(string Name, int Seed, float CellSize, float Jitter, float? MaxHeight,
    float ScaleMin, float ScaleMax, IReadOnlyList<ScatterRuleInfo> Rules);

/// <summary>A named companion layer read back for <see cref="ProceduralInfo"/>, full field fidelity.
/// <see cref="HostKinds"/> is a plain id list (companion hosts carry no weights), <see cref="Kinds"/> is the
/// <c>"id"</c> / <c>"id:weight"</c> convention. <see cref="HostKindsMatchHost"/> is computed: true when
/// <see cref="HostKinds"/> is empty (matches every host placement) or intersects the union of every kind id the
/// host layer's rules can place, ordinal. False flags the silent-no-op mismatch the editor's own warning row
/// surfaces, so an MCP client can detect it without re-deriving the host layer's rule kinds itself.</summary>
public sealed record CompanionLayerInfo(string Name, string HostLayer, int Seed, IReadOnlyList<string> HostKinds,
    IReadOnlyList<string> Kinds, int CountMin, int CountMax, float RadiusMin, float RadiusMax,
    float ScaleMin, float ScaleMax, float? MaxHeight, bool HostKindsMatchHost);

/// <summary>The full procedural setup of the open document: terrain scalars, biome bands, scatter layers (with
/// their rules and kinds), and companion layers. This is the MCP read path for everything the Task 1-4 GUI
/// surface can write (<c>terrain_edit</c>, the biome band triad, and the scatter/companion layer triads),
/// so an agent can inspect the current procedural setup without re-deriving it from raw document JSON.</summary>
public sealed record ProceduralInfo(TerrainInfo Terrain, IReadOnlyList<BiomeBandInfo> Bands,
    IReadOnlyList<ScatterLayerInfo> ScatterLayers, IReadOnlyList<CompanionLayerInfo> CompanionLayers);

/// <summary>One scatter exclusion read back for <see cref="ExclusionsInfo"/>. <see cref="Index"/> is the
/// exclusion's list position, the same key <c>exclusion_edit</c>/<c>exclusion_remove</c>/<c>exclusion_rename</c>/
/// <c>exclusion_set_layers</c> take. <see cref="Name"/> is null for an unnamed exclusion. <see cref="ShapeKind"/>
/// is <c>"disc"</c>/<c>"rect"</c>/<c>"polygon"</c>/<c>"(none)"</c> and <see cref="ShapeSummary"/> is a compact
/// human string of the shape's numbers (disc: center and radius, rect: min and max corners, polygon: point
/// count). <see cref="Layers"/> null means the exclusion applies to every scatter layer, the same convention
/// <see cref="KhaozEngine.MapDoc.MapExclusion.Layers"/> uses.</summary>
public sealed record ExclusionInfo(int Index, string? Name, string ShapeKind, string ShapeSummary,
    IReadOnlyList<string>? Layers);

/// <summary>The scatter exclusions of the open document, in document order (<see cref="ExclusionInfo.Index"/> is
/// the list position). The MCP read counterpart to <c>exclusion_add</c>/<c>exclusion_edit</c>/
/// <c>exclusion_remove</c>/<c>exclusion_rename</c>/<c>exclusion_set_layers</c>.</summary>
public sealed record ExclusionsInfo(IReadOnlyList<ExclusionInfo> Exclusions);

/// <summary>One scatter override read back for <see cref="ScatterOverridesInfo"/>, full field fidelity.
/// <see cref="Index"/> is the override's list position, the same key <c>scatter_override_edit</c>/
/// <c>scatter_override_remove</c>/<c>scatter_override_rename</c>/<c>scatter_override_reorder</c> take, and
/// document order is significant: the first override whose shape covers a point wins over any later one covering
/// the same point. <see cref="ShapeKind"/>/<see cref="ShapeSummary"/> mirror <see cref="ExclusionInfo"/>'s shape
/// fields. <see cref="Kinds"/> is null when the override carries no kind substitution, otherwise the
/// <c>"id"</c> (weight 1) / <c>"id:weight"</c> convention <see cref="QueryService.FormatKinds"/> writes for
/// scatter/companion layer kinds elsewhere. <see cref="Layers"/> null means the override applies to every scatter
/// layer.</summary>
public sealed record ScatterOverrideInfo(int Index, string? Name, string ShapeKind, string ShapeSummary,
    float DensityMultiplier, IReadOnlyList<string>? Kinds, IReadOnlyList<string>? Layers);

/// <summary>The scatter overrides of the open document, in document order (<see cref="ScatterOverrideInfo.Index"/>
/// is the list position, and that order is first-match-wins significant). The MCP read counterpart to
/// <c>scatter_override_add</c>/<c>scatter_override_edit</c>/<c>scatter_override_remove</c>/
/// <c>scatter_override_rename</c>/<c>scatter_override_reorder</c>.</summary>
public sealed record ScatterOverridesInfo(IReadOnlyList<ScatterOverrideInfo> ScatterOverrides);

/// <summary>Result of a <c>sculpt_apply</c> brush dab: the resolved brush name, the dab's centre and radius, how
/// many sculpt cells it actually changed, and the min/max delta among those changed cells (meters, null when
/// nothing changed). <see cref="Applied"/> is false for a clean no-op (a non-positive radius or dt, or a footprint
/// entirely outside the document's paintable sculpt bounds), in which case the other numeric fields are all
/// zero/null and the document is left untouched.</summary>
public sealed record SculptApplyResult(string Brush, float X, float Z, float Radius, int TouchedCellCount,
    float? DeltaMin, float? DeltaMax, bool Applied);

/// <summary>Result of a <c>sculpt_flatten_region</c> call: the flattened rect and target height, how many sculpt
/// cells actually changed, and the min/max delta among those changed cells (meters, null when nothing changed).
/// <see cref="Applied"/> is false for a clean no-op (a degenerate or already-flat region), in which case the
/// document is left untouched.</summary>
public sealed record SculptFlattenRegionResult(float MinX, float MinZ, float MaxX, float MaxZ, float TargetHeight,
    int TouchedCellCount, float? DeltaMin, float? DeltaMax, bool Applied);

/// <summary>Result of a <c>sculpt_clear</c> call: how many sculpt tiles were removed. <see cref="Applied"/> is
/// false for a clean no-op (no sculpt layer, or a region that touches no stored tile), in which case the document
/// is left untouched.</summary>
public sealed record SculptClearResult(int TilesRemoved, bool Applied);

/// <summary>Result of <c>sculpt_stats</c>: the sculpt layer's shape (whether one exists at all, and its cell size),
/// how many tiles are stored, how many cells across those tiles actually carry a nonzero delta, and the min/max
/// delta among those touched cells. <see cref="HasLayer"/> false means the document has no sculpt layer at all (a
/// v1-shaped document, or one never sculpted), in which case every other field is its default
/// (<see cref="CellSize"/> 0, counts 0, min/max null).</summary>
public sealed record SculptStatsResult(bool HasLayer, float CellSize, int TileCount, int TouchedCellCount,
    float? DeltaMin, float? DeltaMax);
