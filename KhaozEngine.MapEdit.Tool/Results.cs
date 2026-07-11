using System.Collections.Generic;

namespace KhaozEngine.MapEdit;

/// <summary>Result of opening or creating a document: the resolved path, its identity, and a full summary.</summary>
public sealed record OpenResult(string Path, string Id, string DisplayName, MapSummary Summary);

/// <summary>Result of a save: the path written and whether it was saved.</summary>
public sealed record SaveResult(string Path, bool Saved);

/// <summary>Result of validation: structural (semantic) validity and JSON-schema validity, each with its
/// errors. When the document is structurally invalid the schema check is skipped and its errors carry a note.</summary>
public sealed record ValidateResult(bool StructuralValid, IReadOnlyList<string> StructuralErrors,
    bool SchemaValid, IReadOnlyList<string> SchemaErrors);

/// <summary>A flat snapshot of the open document: identity, bounds, terrain seed and water level, the feature
/// types in fold order, layer and companion names, section counts, region names, and the dirty flag. Kept flat
/// so it serializes cleanly to the MCP client.</summary>
public sealed record MapSummary(string Id, string DisplayName, int FormatVersion,
    float MinX, float MinZ, float MaxX, float MaxZ,
    int Seed, float WaterLevel,
    IReadOnlyList<string> FeatureTypes,
    IReadOnlyList<string> ScatterLayers, IReadOnlyList<string> CompanionLayers,
    int ExclusionCount, int ScatterOverrideCount,
    int PlacementCount, int SpawnCount, IReadOnlyList<string> RegionNames,
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

/// <summary>Placements and spawns whose position falls inside a query rect (inclusive bounds).</summary>
public sealed record PlacementsInRectResult(IReadOnlyList<PlacementEntry> Placements, IReadOnlyList<SpawnEntry> Spawns);

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
