using System;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Prefabs;
using System.Linq;

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
    /// <summary>Canonical-only clearance provenance for a named compatibility surface. The shared physical selector
    /// correctly returns NoSupport on a NoDraw/zero-underlay cell. Foliage's released logical cell still has that
    /// surface as its owner, exactly as the prepared sampler does. No other empty or refused answer falls back.</summary>
    internal static MapSupportResult ForClearance(MapScopedSurfaces view, MapSupportBinding binding, MapExactXz point, MapSupportResult result)
    {
        if (result.Status != MapSupportStatus.NoSupport || binding is not { Kind: MapSupportBindingKind.Surface, SurfaceId: { } id }) return result;
        MapSurfaceRef? surface = view.Surfaces.FirstOrDefault(s => s.Id == id && s.Role == MapSurfaceRole.SupportFloor);
        if (surface is null) return result;
        var grid = new MapFoliageSupportGrid(surface, view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, id));
        var (x, z, _, _) = grid.Locate(point.X.ToSingle(), point.Z.ToSingle());
        MapPatchKey key = MapPatchKey.ForCell(id, x, z);
        if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) || patch is null) return result;
        int lx = (int)(x - key.SlotX * MapPatchKey.SlotCells) - patch.CellMinX;
        int lz = (int)(z - key.SlotZ * MapPatchKey.SlotCells) - patch.CellMinZ;
        if (lx < 0 || lz < 0 || lx >= patch.Width || lz >= patch.Depth || !patch.IsPresent(lx, lz) ||
            !MapSurfaceCompiler.Fallback(surface, patch.Cells[lz * patch.Width + lx])) return result;
        return new(MapSupportStatus.LegacyFallback, null, 0, null,
            new MapLegacyCellTag(key, MapLowerCellClassifier.SlotCell(x, z), MapFoliageValidation.LegacyTilePolicy),
            result.SpaceId, result.Via, null, result.Witness);
    }

}
