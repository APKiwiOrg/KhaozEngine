using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Constructs in-memory layer defects without first passing through the closed JSON admission gate.</summary>
internal sealed class FoliageLayerDraft(MapFoliageLayer source)
{
    internal int PayloadVersion = source.PayloadVersion;
    internal float OriginX = source.OriginX, OriginZ = source.OriginZ, CellSize = source.CellSize;
    internal int Width = source.Width, Depth = source.Depth;
    internal MapRowDirection RowDirection = source.RowDirection;
    internal float Spacing = source.Spacing, ScaleMin = source.ScaleMin, ScaleMax = source.ScaleMax, RootOffset = source.RootOffset;
    internal float DoorClearance = source.DoorClearance, EdgeFade = source.EdgeFade;
    internal MapFoliagePolicy Policy = source.Policy;
    internal IReadOnlyList<MapFoliageModel> Models = source.Models;
    internal IReadOnlyList<MapFoliageMaterial> Materials = source.Materials;
    internal IReadOnlyList<ushort> AllowedVisibleMaterials = source.AllowedVisibleMaterials;
    internal IReadOnlyList<MapFoliageCompatibilityOrdinal> CompatibilityOrdinals = source.CompatibilityOrdinals;
    internal IReadOnlyList<MapFoliageClearanceRule> ClearanceRules = source.ClearanceRules;
    internal IReadOnlyList<MapFoliageMask> ExceptionMasks = source.ExceptionMasks;
    internal IReadOnlyList<MapFoliageOwnerSupport> OwnerSupports = source.OwnerSupports;

    internal MapFoliageLayer Build() => new(PayloadVersion, source.Id, source.Support, OriginX, OriginZ, CellSize,
        Width, Depth, RowDirection, source.Seed, Spacing, ScaleMin, ScaleMax, RootOffset, Models,
        AllowedVisibleMaterials, Materials, Policy, source.ExcludeIndoors, source.ExcludeSolidObjects,
        DoorClearance, EdgeFade, CompatibilityOrdinals, ClearanceRules, ExceptionMasks, OwnerSupports, source.Chunks);
}
