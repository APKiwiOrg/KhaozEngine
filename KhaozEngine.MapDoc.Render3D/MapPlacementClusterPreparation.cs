using System;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One before-construction merge envelope and one retained receipt for every native CPU product.</summary>
internal static class MapPlacementClusterPreparation
{
    internal static (NativePropClusterCpuBuild Build, long Bytes) Build(long mergeBytes,
        Func<NativePropClusterRequest> request, Func<NativePropClusterRequest, NativePropClusterCpuBuild> build,
        bool publishToParent = true)
    {
        var admission = MapSceneAdmission.Current!;
        using var preparation = admission.BeginPreparation();
        // The transformed merge, request copies and final weld coexist during construction.
        preparation.Reserve(checked(8 * mergeBytes));
        var result = build(request());
        var inventory = new MapNativeInventory();
        long bytes = checked(MapNativeInventory.Mesh(result.BorrowMesh) + inventory.Layer(result.Request.BorrowLayer, false) +
            inventory.WeldPlacements(result.Request) +
            16L * result.Request.Instances.Count + 8L * result.BorrowContributions.Count +
            result.BorrowContributions.Sum(m => MapNativeInventory.Mesh(m)));
        result.Own(preparation.Complete(bytes, publishToParent));
        return (result, bytes);
    }
}
