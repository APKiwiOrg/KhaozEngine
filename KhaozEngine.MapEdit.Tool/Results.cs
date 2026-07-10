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
