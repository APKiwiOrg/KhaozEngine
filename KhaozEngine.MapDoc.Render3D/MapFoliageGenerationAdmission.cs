using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Checks count ceilings and admits only the first construction control before generation begins.</summary>
internal static class MapFoliageGenerationAdmission
{
    internal static long Begin(MapWindowFoliageContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        MapFoliageLimits limits, long retained, long allowance, MapSceneAdmission? admission)
    {
        ((IMapFoliageInputs)context).RequireChunk(layer, chunk);
        var rectangle = MapFoliageDistribution.Rectangle(layer, chunk);
        var range = MapFoliageLatticeRange.Of(rectangle.MinX, rectangle.MinZ, rectangle.MaxX, rectangle.MaxZ, layer.Spacing);
        if (range.Count > limits.MaxCandidates)
            throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxCandidates), (decimal)range.Count, limits.MaxCandidates);
        long first = MapFoliageInventory.ConstructionControls;
        if (first > allowance - retained)
            throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), (decimal)retained + first, allowance);
        if (first > limits.MaxQueryRetainedBytes)
            throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), first, limits.MaxQueryRetainedBytes);
        admission?.ReserveBytes(first);
        return admission is null ? 0 : first;
    }
    internal static void Charge(MapSceneAdmission admission, long bytes) => admission.ReserveBytes(bytes);
}
