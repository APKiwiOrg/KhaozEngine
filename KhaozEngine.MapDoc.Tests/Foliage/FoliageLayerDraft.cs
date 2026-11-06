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

    internal MapFoliageLayer Build() => new(PayloadVersion, source.Id, source.Support, OriginX, OriginZ, CellSize,
        Width, Depth, RowDirection, source.Seed, Spacing, ScaleMin, ScaleMax, RootOffset, source.Models,
        source.AllowedVisibleMaterials, source.Materials, source.Policy, source.ExcludeIndoors, source.ExcludeSolidObjects,
        source.DoorClearance, source.EdgeFade, source.CompatibilityOrdinals, source.ClearanceRules, source.ExceptionMasks,
        source.OwnerSupports, source.Chunks);
}
