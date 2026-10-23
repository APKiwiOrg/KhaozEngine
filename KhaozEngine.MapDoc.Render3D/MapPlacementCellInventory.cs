using System;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Certifies full logical cell membership before a window may publish its canonical proxy.</summary>
internal static class MapPlacementCellInventory
{
    internal static void RequireComplete(MapScopedScene scene, MapPlacementRouting routing, NativePropClusterRequest request)
    {
        var layer = routing.ClusterLayer(request.Key.LayerId)
            ?? throw new MapDocumentException("Native cluster has no matching caller layer.");
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        var result = scene.AcquireDependencies(scene.Scope with
        {
            DrawBounds = MapPlacementCells.Coverage(request.Key, scene.Source.Data.TileSize),
            Channels = MapSceneChannels.Placements | MapSceneChannels.Annotations,
            MotionPaddingMetres = 0
        });
        var complete = result.Scene ?? throw new MapSceneSourceException(result.Status, "Native cluster cell discovery refused.");
        admission.ReserveBytes(checked(128L + 64L * complete.Placements.Count));
        var expected = complete.Placements.Where(p => layer.AssetIds.Contains(p.AssetId) &&
                MapPlacementCells.Key(layer.Id, NativeMapPlacements.Pose(complete, p), scene.Source.Data.TileSize) == request.Key)
            .Select(p => p.PlacementId).Order(StringComparer.Ordinal);
        if (!expected.SequenceEqual(request.Instances.Select(i => i.Id)))
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid,
                "Native HLOD requires the complete canonical cell inventory. Acquire every cell member before merging.");
    }
}
