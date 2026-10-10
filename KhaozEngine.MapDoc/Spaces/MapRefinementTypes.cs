using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

public enum MapRefinementStatus { Complete, CapacityExceeded, NotRepresentable, Invalid, MissingGeometry }

/// <summary>The context defaults are configurable operational query budgets counted from cell bytes before compilation, not world limits or byte caps.</summary>
public sealed record MapRefinementLimits(int MaxSourceFacesPerBound = 16_384, int MaxPairChecks = 65_536,
    int MaxRefinementFaces = 32_768, int MaxRefinementVertices = 131_072,
    int MaxContextPatches = 256, int MaxContextFaces = 262_144);

public readonly record struct MapBoundFace(MapFaceKey Key, MapExactTriangle Triangle);
public sealed record MapBoundFaceSet(MapRefinementStatus Status, IReadOnlyList<MapBoundFace> Faces,
    IReadOnlyList<MapLegacyCellTag> Compatibility, string? Detail);
public sealed record MapRefinementFace(MapFaceKey Lower, MapFaceKey? Upper,
    IReadOnlyList<MapExactXz> Polygon, MapExactValue? MinSeparation);
public sealed record MapCellRefinement(MapRefinementStatus Status, IReadOnlyList<MapExactXz> Vertices,
    IReadOnlyList<MapRefinementFace> Faces, MapExactValue CellArea, MapExactValue LowerArea,
    MapExactValue CompatibilityArea, IReadOnlyList<MapLegacyCellTag> CompatibilityCells,
    MapExactValue? UpperArea, MapExactValue? MinSeparation, int PairChecks, string? Detail);

internal sealed class MapRefinementWork
{
    internal long ChargedPairs;
    internal long BoundingBoxComparisons;
    internal long PositiveBoundingBoxPairs;
    internal long PositiveIntersections;
}

internal readonly record struct MapCellDemand(MapPatchKey Patch, int SlotCell);
internal readonly record struct MapBoundFaceDemand(MapCellDemand Cell, MapRecordRef? Opening);
internal sealed record MapFootprintPreparation(MapBoundFaceContext? Context, string? Refusal,
    IReadOnlyDictionary<(string FootprintId, int SlotCell), MapCellRefinement> CellOutcomes);

internal sealed class MapBoundFaceWork
{
    internal int ContextsPrepared;
    internal int PatchValidations;
    internal long BoundFacesCounted;
    internal long ContextFacesCounted;
    internal int Compiles;
    internal long CompiledFaces;
}

public enum MapLowerCellClass : byte { Physical, LegacyNonCapture, KnownHole, UntaggedLegacy, Unavailable, InvalidRecipe }
public readonly record struct MapLegacyCellTag(MapPatchKey Patch, int SlotCell, string Policy);
public sealed record MapLowerCellClassification(MapLowerCellClass Class, MapPatchKey Patch,
    int SlotCell, MapLegacyCellTag? Legacy, string? Detail);
