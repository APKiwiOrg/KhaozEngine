using System;
using KhaozEngine.MapDoc.Support;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The canonical support identity of a foliage sample (R7-R4). Surface and Space bindings both normalize to the
/// owner of the selected support surface. A logical NoDraw or zero-underlay cell is a LegacyFallback compatibility
/// support and names its compatibility patch's surface, never a capture support. The occupied space is a separate fact
/// and never replaces this identity. Clearance resolves owner bindings through this normalization and never compares
/// binding text IDs.</summary>
internal static class MapFoliageSupportIdentity
{
    /// <summary>The selected face owner of a Supported result, the compatibility surface of a LegacyFallback result,
    /// and null for every refusal or empty answer.</summary>
    internal static string? From(MapSupportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            MapSupportStatus.Supported => result.Face?.OwnerId,
            MapSupportStatus.LegacyFallback => result.Compatibility?.Patch.SurfaceId,
            _ => null,
        };
    }
}
