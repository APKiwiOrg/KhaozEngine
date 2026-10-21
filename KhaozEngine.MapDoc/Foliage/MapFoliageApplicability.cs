using System;
using System.Linq;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Compatibility provenance, independent of an owner's height, clearing target or occupied room.</summary>
internal static class MapFoliageApplicability
{
    internal static bool Applies(MapFoliageLayer layer, string ownerSeatedSurfaceId, string sampleSupportSurfaceId,
        MapFoliageClearanceKind kind)
    {
        return Applies(layer.CompatibilityOrdinals.FirstOrDefault(r => r.SurfaceId == ownerSeatedSurfaceId)?.Ordinal,
            layer.CompatibilityOrdinals.FirstOrDefault(r => r.SurfaceId == sampleSupportSurfaceId)?.Ordinal,
            ownerSeatedSurfaceId, sampleSupportSurfaceId, kind);
    }

    internal static bool Applies(IReadOnlyDictionary<string, int> ordinals, string ownerSeatedSurfaceId, string sampleSupportSurfaceId,
        MapFoliageClearanceKind kind) => Applies(ordinals.TryGetValue(ownerSeatedSurfaceId, out int o) ? o : null,
            ordinals.TryGetValue(sampleSupportSurfaceId, out int s) ? s : null, ownerSeatedSurfaceId, sampleSupportSurfaceId, kind);

    static bool Applies(int? owner, int? sample, string ownerSeatedSurfaceId, string sampleSupportSurfaceId, MapFoliageClearanceKind kind)
    {
        if (owner is null) return kind != MapFoliageClearanceKind.RoofAbove &&
            string.Equals(ownerSeatedSurfaceId, sampleSupportSurfaceId, StringComparison.Ordinal);
        if (sample is null) return false;
        return kind == MapFoliageClearanceKind.RoofAbove ? owner > sample : owner == sample;
    }
}
